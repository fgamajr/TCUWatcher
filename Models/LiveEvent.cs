using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace TCUWatcher.API.Models;

public class LiveEvent
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [BsonElement("title")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("videoId")]
    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;

    [BsonElement("startedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [BsonElement("endedAt")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    [JsonPropertyName("endedAt")]
    [BsonIgnoreIfNull]
    public DateTime? EndedAt { get; set; }

    [BsonElement("url")]
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [BsonElement("channelId")]
    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = string.Empty;

    [BsonElement("isLive")]
    [JsonPropertyName("isLive")]
    public bool IsLive { get; set; }

    [BsonElement("missCount")]
    [JsonPropertyName("missCount")]
    public int MissCount { get; set; }

    [BsonIgnore]
    [JsonIgnore]
    public string FormattedDate { get; set; } = string.Empty;
}
