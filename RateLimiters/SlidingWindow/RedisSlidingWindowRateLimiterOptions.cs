using RedisRateLimiting;

namespace RateLimiters.Test.SlidingWindow;

/// <summary>
/// Options to specify the behavior of a <see cref="RedisSlidingWindowRateLimiterOptions"/>.
/// </summary>
public sealed class RedisSlidingWindowRateLimiterOptions : RedisRateLimiterOptions
{
    /// <summary>
    /// Specifies the time window that takes in the requests.
    /// Must be set to a value greater than <see cref="TimeSpan.Zero" /> by the time these options are passed to the constructor of <see cref="RedisRateLimiting.RedisSlidingWindowRateLimiter{TKey}"/>.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of permit counters that can be allowed in a window.
    /// Must be set to a value > 0 by the time these options are passed to the constructor of <see cref="RedisRateLimiting.RedisSlidingWindowRateLimiter{TKey}"/>.
    /// </summary>
    public int PermitLimit { get; set; }

    // todo: пофиксить описание
    /// <summary>
    /// Maximum number of permits that can be queued concurrently.
    /// Must be set to a value >= 0 by the time these options are passed to the constructor of <see cref="RedisConcurrencyRateLimiter{TKey}"/>.
    /// </summary>
    public int QueueLimit { get; set; }

    // todo: пофиксить описание
    /// <summary>
    /// Specifies the minimum period between trying to dequeue queued permits.
    /// Must be set to a value greater than <see cref="TimeSpan.Zero" /> by the time these options are passed to the constructor of <see cref="RedisConcurrencyRateLimiter{TKey}"/>.
    /// </summary>
    public TimeSpan TryDequeuePeriod { get; set; } = TimeSpan.FromSeconds(1);
}
