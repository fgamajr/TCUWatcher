using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System; // Para DayOfWeek

namespace TCUWatcher.API.Models;

public class MonitoringWindow
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("dayOfWeek")]
    [BsonRepresentation(BsonType.Int32)] // Armazena o enum DayOfWeek como inteiro
    public DayOfWeek DayOfWeek { get; set; } // System.DayOfWeek (Domingo=0, ..., Sábado=6)

    [BsonElement("startTimeBrasilia")] // Formato "HH:mm"
    public string StartTimeBrasilia { get; set; } = string.Empty;

    [BsonElement("endTimeBrasilia")] // Formato "HH:mm"
    public string EndTimeBrasilia { get; set; } = string.Empty;

    [BsonElement("isEnabled")]
    public bool IsEnabled { get; set; } = true;

    [BsonElement("description")]
    [BsonIgnoreIfNull]
    public string? Description { get; set; }
}