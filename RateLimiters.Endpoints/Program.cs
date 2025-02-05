using System.Net;
using MongoDB.Driver;
using RateLimiters.Endpoints;
using RedisRateLimiting.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redisOptions = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")!);
var connectionMultiplexer = ConnectionMultiplexer.Connect(redisOptions);

// Регистрация MongoDB
var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDB");
var mongoClient = new MongoClient(mongoConnectionString);
var database = mongoClient.GetDatabase("RequestLog"); // Название базы данных
builder.Services.AddSingleton(database);

builder.Services.AddSingleton<IConnectionMultiplexer>(sp => connectionMultiplexer);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, 8086); // слушаем все доступные IP на порту 8080
});

builder.Services.AddRateLimiter(options =>
{
    options.AddRedisSlidingWindowLimiter(
        "demo_sliding_window",
        (opt) =>
        {
            opt.ConnectionMultiplexerFactory = () => connectionMultiplexer;
            opt.PermitLimit = 1;
            opt.Window = TimeSpan.FromSeconds(20);
            opt.QueueLimit = 1;
        }
    );
    options.OnRejected = (context, ct) =>
        RateLimitMetadata.OnRejected(context.HttpContext, context.Lease, ct);
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseRateLimiter();

app.UseSwagger();
app.UseSwaggerUI();

//app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();

using var scope = app.Services.CreateScope();

app.Run();

// Hack: make the implicit Program class public so test projects can access it
public partial class Program
{
    protected Program() { }
}
