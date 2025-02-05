using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.RateLimiting;
using RedisRateLimiting;

namespace RateLimiters.Test.SlidingWindow;

public class RedisSlidingWindowRateLimiter<TKey> : System.Threading.RateLimiting.RateLimiter
{
    private readonly RedisSlidingWindowManager _redisManager;
    private readonly RedisSlidingWindowRateLimiterOptions _options;
    private readonly ConcurrentQueue<Request> _queue = new();
    private readonly PeriodicTimer? _periodicTimer;
    private bool _disposed;

    private readonly SlidingWindowLease FailedLease = new(isAcquired: false, null);

    private int _activeRequestsCount;
    private long _idleSince = Stopwatch.GetTimestamp();

    private sealed class Request
    {
        public CancellationToken CancellationToken { get; set; }

        public SlidingWindowLeaseContext? LeaseContext { get; set; }

        public TaskCompletionSource<SlidingWindowLease>? TaskCompletionSource { get; set; }

        public CancellationTokenRegistration CancellationTokenRegistration { get; set; }
    }

    public override TimeSpan? IdleDuration =>
        Interlocked.CompareExchange(ref _activeRequestsCount, 0, 0) > 0 ? null : Stopwatch.GetElapsedTime(_idleSince);

    public RedisSlidingWindowRateLimiter(TKey partitionKey, RedisSlidingWindowRateLimiterOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (options.PermitLimit <= 0)
        {
            throw new ArgumentException(
                $"{nameof(options.PermitLimit)} must be set to a value greater than 0.",
                nameof(options)
            );
        }
        if (options.Window <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(options.Window)} must be set to a value greater than TimeSpan.Zero.",
                nameof(options)
            );
        }
        if (options.ConnectionMultiplexerFactory is null)
        {
            throw new ArgumentException(
                $"{nameof(options.ConnectionMultiplexerFactory)} must not be null.",
                nameof(options)
            );
        }

        _options = new RedisSlidingWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit,
            QueueLimit = options.QueueLimit,
            Window = options.Window,
            ConnectionMultiplexerFactory = options.ConnectionMultiplexerFactory,
        };

        if (_options.QueueLimit > 0)
        {
            _periodicTimer = new PeriodicTimer(_options.TryDequeuePeriod);
            _ = StartDequeueTimerAsync(_periodicTimer);
        }
        _redisManager = new RedisSlidingWindowManager(partitionKey?.ToString() ?? string.Empty, _options);
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        return _redisManager.GetStatistics();
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken
    )
    {
        _idleSince = Stopwatch.GetTimestamp();
        if (permitCount > _options.PermitLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                permitCount,
                string.Format("{0} permit(s) exceeds the permit limit of {1}.", permitCount, _options.PermitLimit)
            );
        }

        Interlocked.Increment(ref _activeRequestsCount);
        try
        {
            return await AcquireAsyncCoreInternal(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequestsCount);
            _idleSince = Stopwatch.GetTimestamp();
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        // https://github.com/cristipufu/aspnetcore-redis-rate-limiting/issues/66
        return FailedLease;
    }

    private async ValueTask<RateLimitLease> AcquireAsyncCoreInternal(CancellationToken cancellationToken = default)
    {
        var leaseContext = new SlidingWindowLeaseContext
        {
            Limit = _options.PermitLimit,
            Window = _options.Window,
            RequestId = Guid.NewGuid().ToString(),
        };

        RedisSlidingWindowResponse response = await _redisManager.TryAcquireLeaseAsync(leaseContext.RequestId);

        leaseContext.Count = response.Count;
        leaseContext.Allowed = response.Allowed;

        if (leaseContext.Allowed)
        {
            return new SlidingWindowLease(isAcquired: true, leaseContext);
        }

        if (response.Queued)
        {
            Request request =
                new()
                {
                    CancellationToken = cancellationToken,
                    LeaseContext = leaseContext,
                    TaskCompletionSource = new TaskCompletionSource<SlidingWindowLease>(),
                };

            if (cancellationToken.CanBeCanceled)
            {
                request.CancellationTokenRegistration = cancellationToken.Register(
                    static obj =>
                    {
                        // When the request gets canceled
                        var request = (Request)obj!;
                        request.TaskCompletionSource!.TrySetCanceled(request.CancellationToken);
                    },
                    request
                );
            }

            _queue.Enqueue(request);

            return await request.TaskCompletionSource.Task;
        }

        return new SlidingWindowLease(isAcquired: false, leaseContext);
    }

    private async Task StartDequeueTimerAsync(PeriodicTimer periodicTimer)
    {
        while (await periodicTimer.WaitForNextTickAsync())
        {
            await TryDequeueRequestsAsync();
        }
    }

    private async Task TryDequeueRequestsAsync()
    {
        try
        {
            while (_queue.TryPeek(out var request))
            {
                if (request.TaskCompletionSource!.Task.IsCompleted)
                {
                    try
                    {
                        // The request was canceled while in the pending queue
                        await _redisManager.ReleaseQueueLeaseAsync(request.LeaseContext!.RequestId!);
                    }
                    finally
                    {
                        await request.CancellationTokenRegistration.DisposeAsync();

                        _queue.TryDequeue(out _);
                    }

                    continue;
                }

                var response = await _redisManager.TryAcquireLeaseAsync(request.LeaseContext!.RequestId!);

                request.LeaseContext.Count = response.Count;

                if (response.Allowed)
                {
                    var pendingLease = new SlidingWindowLease(isAcquired: true, request.LeaseContext);

                    try
                    {
                        if (request.TaskCompletionSource?.TrySetResult(pendingLease) == false)
                        {
                            // The request was canceled after we acquired the lease
                            await _redisManager.ReleaseLeaseAsync(request.LeaseContext!.RequestId!);
                        }

                        pendingLease.Dispose();
                    }
                    finally
                    {
                        await request.CancellationTokenRegistration.DisposeAsync();

                        _queue.TryDequeue(out _);
                    }
                }
                else
                {
                    // Try next time
                    break;
                }
            }
        }
        catch
        {
            // Try next time
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _periodicTimer?.Dispose();

        while (_queue.TryDequeue(out var request))
        {
            request?.CancellationTokenRegistration.Dispose();
            request?.TaskCompletionSource?.TrySetResult(FailedLease);
        }

        base.Dispose(disposing);
    }

    private sealed class SlidingWindowLeaseContext
    {
        public long Count { get; set; }

        public long Limit { get; set; }

        public TimeSpan Window { get; set; }

        public bool Allowed { get; set; }

        public string? RequestId { get; set; }
    }

    private sealed class SlidingWindowLease : RateLimitLease
    {
        private static readonly string[] s_allMetadataNames = new[]
        {
            RateLimitMetadataName.Limit.Name,
            RateLimitMetadataName.Remaining.Name,
        };

        private readonly SlidingWindowLeaseContext? _context;

        public SlidingWindowLease(bool isAcquired, SlidingWindowLeaseContext? context)
        {
            IsAcquired = isAcquired;
            _context = context;
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames => s_allMetadataNames;

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == RateLimitMetadataName.Limit.Name && _context is not null)
            {
                metadata = _context.Limit.ToString();
                return true;
            }

            if (metadataName == RateLimitMetadataName.Remaining.Name && _context is not null)
            {
                metadata = Math.Max(_context.Limit - _context.Count, 0);
                return true;
            }

            metadata = default;
            return false;
        }
    }
}
