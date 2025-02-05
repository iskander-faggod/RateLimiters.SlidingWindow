using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Driver;
using RateLimiters.Endpoints.Models;
using RedisRateLimiting.Sample;

namespace RateLimiters.Endpoints.Controllers;

[ApiController]
[Route("[controller]")]
[EnableRateLimiting("demo_sliding_window")]
public class SlidingWindowController : ControllerBase
{
    private readonly IMongoCollection<RequestLog> _requestLogs;

    public SlidingWindowController(IMongoDatabase database)
    {
        _requestLogs = database.GetCollection<RequestLog>("RequestLogs");
    }

    private static readonly string[] Summaries = new[]
    {
        "Freezing",
        "Bracing",
        "Chilly",
        "Cool",
        "Mild",
        "Warm",
        "Balmy",
        "Hot",
        "Sweltering",
        "Scorching",
    };

    [HttpGet]
    public async Task<IEnumerable<WeatherForecast>> Get()
    {
        var log = new RequestLog
        {
            InstanceId = Environment.MachineName, // Идентификатор инстанса (имя машины)
            RequestTime = DateTime.UtcNow,
            Path = HttpContext.Request.Path,
        };

        await _requestLogs.InsertOneAsync(log);

        return Enumerable
            .Range(1, 5)
            .Select(index => new WeatherForecast
            {
                Date = DateTime.Now.AddDays(index),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)],
            })
            .ToArray();
    }
}
