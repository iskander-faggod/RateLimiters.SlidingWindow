using MongoDB.Bson;

namespace RateLimiters.Endpoints.Models;

public class RequestLog
{
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();
    public string InstanceId { get; set; } // Идентификатор инстанса
    public DateTime RequestTime { get; set; } // Время запроса
    public string Path { get; set; } // Путь запроса (например, "/slidingwindow")
}
