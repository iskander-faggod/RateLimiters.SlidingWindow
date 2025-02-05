using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace RateLimiters.Test;

/// <summary>
/// Тестирование механизма Sliding Window Rate Limiter
/// </summary>
public class SlidingWindowIntegrationTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly string _apiPath = "/SlidingWindow";

    public SlidingWindowIntegrationTest(WebApplicationFactory<Program> factory, ITestOutputHelper testOutputHelper)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5012") };
        _testOutputHelper = testOutputHelper;
    }

    [Fact(DisplayName = "Sliding Window with no Queue")]
    public async Task EnforceRateLimitsWithSlidingWindow_NoQueue()
    {
        // Первая попытка: должна пройти успешно
        var response = await MakeRequestAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Вторая попытка: лимит превышен
        response = await MakeRequestAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, response.Limit);
        Assert.Equal(0, response.Remaining);

        // Задержка для сброса окна
        await Task.Delay(3000);

        // Повторная попытка после задержки: должна пройти успешно
        response = await MakeRequestAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(DisplayName = "Sliding Window with Queue with 1 permit, 1 queue and 30sec window")]
    public async Task EnforceRateLimitsWithSlidingWindow_Queue()
    {
        // Первая попытка: должна пройти успешно
        var response = await MakeRequestAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Вторая попытка: попадает в очередь и ждем 30 sec
        response = await MakeRequestAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Третья попытка после задержки: должна пройти успешно
        response = await MakeRequestAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<RateLimitResponse> MakeRequestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _apiPath);
        using var response = await _httpClient.SendAsync(request);

        var rateLimitResponse = new RateLimitResponse { StatusCode = response.StatusCode };

        if (
            response.Headers.TryGetValues(RateLimitHeaders.Limit, out var limitValues)
            && long.TryParse(limitValues.FirstOrDefault(), out var limit)
        )
        {
            rateLimitResponse.Limit = limit;
        }

        if (
            response.Headers.TryGetValues(RateLimitHeaders.Remaining, out var remainingValues)
            && long.TryParse(remainingValues.FirstOrDefault(), out var remaining)
        )
        {
            rateLimitResponse.Remaining = remaining;
        }

        if (
            response.Headers.TryGetValues(RateLimitHeaders.RetryAfter, out var retryAfterValues)
            && int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfter)
        )
        {
            rateLimitResponse.RetryAfter = retryAfter;
        }

        _testOutputHelper.WriteLine($"Response: {rateLimitResponse}");
        return rateLimitResponse;
    }

    private sealed class RateLimitResponse
    {
        public HttpStatusCode StatusCode { get; set; }
        public long? Limit { get; set; }
        public long? Remaining { get; set; }
        public int? RetryAfter { get; set; }

        public override string ToString()
        {
            return $"StatusCode: {StatusCode}, Limit: {Limit}, Remaining: {Remaining}, RetryAfter: {RetryAfter}";
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
