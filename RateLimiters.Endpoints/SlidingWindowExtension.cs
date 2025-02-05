using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using RateLimiters.Test.SlidingWindow;

namespace RateLimiters.Endpoints;

public static class SlidingWindowExtension
{
    public static RateLimiterOptions AddRedisSlidingWindowLimiter(
        this RateLimiterOptions options,
        string policyName,
        Action<RedisSlidingWindowRateLimiterOptions> configureOptions
    )
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        var key = new PolicyNameKey() { PolicyName = policyName };
        var slidingWindowRateLimiterOptions = new RedisSlidingWindowRateLimiterOptions();
        configureOptions.Invoke(slidingWindowRateLimiterOptions);

        return options.AddPolicy(
            policyName,
            context =>
            {
                return GetSlidingWindowRateLimiter(key, _ => slidingWindowRateLimiterOptions);
            }
        );
    }

    public static RateLimitPartition<TKey> GetSlidingWindowRateLimiter<TKey>(
        TKey partitionKey,
        Func<TKey, RedisSlidingWindowRateLimiterOptions> factory
    )
    {
        return RateLimitPartition.Get(partitionKey, key => new RedisSlidingWindowRateLimiter<TKey>(key, factory(key)));
    }

    internal sealed class PolicyNameKey
    {
        public required string PolicyName { get; init; }

        public override bool Equals(object? obj)
        {
            if (obj is PolicyNameKey key)
            {
                return PolicyName == key.PolicyName;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return PolicyName.GetHashCode();
        }

        public override string ToString()
        {
            return PolicyName;
        }
    }
}
