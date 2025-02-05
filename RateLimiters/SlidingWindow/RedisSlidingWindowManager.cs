using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace RateLimiters.Test.SlidingWindow;

internal class RedisSlidingWindowManager
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly RedisSlidingWindowRateLimiterOptions _options;
    private readonly RedisKey _rateLimitKey;
    private readonly RedisKey _statsRateLimitKey;
    private readonly RedisKey _queueRateLimitKey;

    private static readonly LuaScript Script = LuaScript.Prepare(
        @"local limit = tonumber(@permit_limit)
        local queue_limit = tonumber(@queue_limit)
        local timestamp = tonumber(@current_time)
        local window = tonumber(@window)

        -- Удаляем все запросы за пределами окна
        redis.call(""zremrangebyscore"", @rate_limit_key, '-inf', timestamp - window)

        if queue_limit > 0
        then
            redis.call(""zremrangebyscore"", @queue_key, '-inf', timestamp - window)
        end

        local count = redis.call(""zcard"", @rate_limit_key)
        local allowed = count < limit
        local queued = false
        local queue_count = 0

        if allowed
        then
            -- Разрешить запрос, так как лимит не превышен
            redis.call(""zadd"", @rate_limit_key, timestamp, @unique_id)
        else
            -- Если лимит превышен, проверяем возможность постановки в очередь
            if queue_limit > 0
            then
                queue_count = redis.call(""zcard"", @queue_key)
                queued = queue_count < queue_limit

                if queued
                then
                    redis.call(""zadd"", @queue_key, timestamp, @unique_id)
                end
            end
        end

        -- Проверяем, можно ли освободить запросы из очереди
        if count < limit and queue_limit > 0
        then
            local queued_requests = redis.call(""zrange"", @queue_key, 0, limit - count - 1)

            for _, request_id in ipairs(queued_requests)
            do
                -- Перемещаем запрос из очереди в основное хранилище
                redis.call(""zrem"", @queue_key, request_id)
                redis.call(""zadd"", @rate_limit_key, timestamp, request_id)
            end
        end

        -- Обновляем статистику
        if allowed
        then
            redis.call(""hincrby"", @stats_key, 'total_successful', 1)
        else
            if not queued
            then
                redis.call(""hincrby"", @stats_key, 'total_failed', 1)
            end
        end

        -- Устанавливаем время жизни ключей
        local expireAtMilliseconds = math.floor((timestamp + window) * 1000 + 1)
        redis.call(""pexpireat"", @rate_limit_key, expireAtMilliseconds)
        redis.call(""pexpireat"", @stats_key, expireAtMilliseconds)
        if queue_limit > 0
        then
            redis.call(""pexpireat"", @queue_key, expireAtMilliseconds)
        end

        -- Возвращаем результат
        return { allowed, count, queued, queue_count }"
    );

    private static readonly LuaScript StatisticsScript = LuaScript.Prepare(
        @"local count = redis.call(""zcard"", @rate_limit_key)
            local total_successful_count = redis.call(""hget"", @stats_key, 'total_successful')
            local total_failed_count = redis.call(""hget"", @stats_key, 'total_failed')

            return { count, total_successful_count, total_failed_count }"
    );

    public RedisSlidingWindowManager(string partitionKey, RedisSlidingWindowRateLimiterOptions options)
    {
        _options = options;
        _connectionMultiplexer = options.ConnectionMultiplexerFactory!.Invoke();

        _rateLimitKey = new RedisKey($"rl:sw:{{{partitionKey}}}");
        _statsRateLimitKey = new RedisKey($"rl:sw:{{{partitionKey}}}:stats");
        _queueRateLimitKey = new RedisKey($"rl:sw:{{{partitionKey}}}:q");
    }

    internal async Task ReleaseQueueLeaseAsync(string requestId)
    {
        var database = _connectionMultiplexer.GetDatabase();
        await database.SortedSetRemoveAsync(_rateLimitKey, requestId);
    }

    internal async Task ReleaseLeaseAsync(string requestId)
    {
        var database = _connectionMultiplexer.GetDatabase();
        await database.SortedSetRemoveAsync(_rateLimitKey, requestId);
    }

    internal async Task<RedisSlidingWindowResponse> TryAcquireLeaseAsync(string requestId)
    {
        var now = DateTimeOffset.UtcNow;
        double nowUnixTimeSeconds = now.ToUnixTimeMilliseconds() / 1000.0;

        var database = _connectionMultiplexer.GetDatabase();

        var response = await database.ScriptEvaluateAsync(
            Script,
            new
            {
                rate_limit_key = _rateLimitKey,
                queue_key = _queueRateLimitKey,
                stats_key = _statsRateLimitKey,
                permit_limit = (RedisValue)_options.PermitLimit,
                queue_limit = (RedisValue)_options.QueueLimit,
                try_enqueue = (RedisValue)1,
                current_time = (RedisValue)nowUnixTimeSeconds,
                unique_id = (RedisValue)requestId,
                window = (RedisValue)_options.Window.TotalSeconds,
            }
        );

        var result = new RedisSlidingWindowResponse();

        if (response != null)
        {
            result.Allowed = (bool)response[0];
            result.Count = (long)response[1];
            result.Queued = (bool)response[2];
            result.QueueCount = (long)response[3];
        }

        return result;
    }

    internal RedisSlidingWindowResponse TryAcquireLease(string requestId)
    {
        var now = DateTimeOffset.UtcNow;
        double nowUnixTimeSeconds = now.ToUnixTimeMilliseconds() / 1000.0;

        var database = _connectionMultiplexer.GetDatabase();

        var response = (RedisValue[]?)
            database.ScriptEvaluate(
                Script,
                new
                {
                    rate_limit_key = _rateLimitKey,
                    stats_key = _statsRateLimitKey,
                    permit_limit = (RedisValue)_options.PermitLimit,
                    queue_limit = (RedisValue)_options.QueueLimit,
                    window = (RedisValue)_options.Window.TotalSeconds,
                    current_time = (RedisValue)nowUnixTimeSeconds,
                    unique_id = (RedisValue)requestId,
                }
            );

        var result = new RedisSlidingWindowResponse();

        if (response != null)
        {
            result.Allowed = (bool)response[0];
            result.Count = (long)response[1];
            result.Queued = (bool)response[2];
            result.QueueCount = (long)response[3];
        }

        return result;
    }

    internal RateLimiterStatistics? GetStatistics()
    {
        var database = _connectionMultiplexer.GetDatabase();

        var response = (RedisValue[]?)
            database.ScriptEvaluate(
                StatisticsScript,
                new { rate_limit_key = _rateLimitKey, stats_key = _statsRateLimitKey }
            );

        if (response == null)
        {
            return null;
        }

        return new RateLimiterStatistics
        {
            CurrentAvailablePermits =
                _options.PermitLimit + _options.QueueLimit - (long)response[0] - (long)response[1],
            TotalSuccessfulLeases = (long)response[1],
            TotalFailedLeases = (long)response[2],
        };
    }
}

internal class RedisSlidingWindowResponse
{
    internal bool Allowed { get; set; }
    internal long Count { get; set; }

    internal bool Queued { get; set; }

    internal long QueueCount { get; set; }
}
