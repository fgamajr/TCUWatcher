using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace TCUWatcher.API.Models;

// Add an enum for processing status
public enum ProcessingStatus
{
    Pending,      // Uploaded, awaiting processing
    Processing,   // Actively being processed (snapshots/audio being extracted)
    CompletedSuccessfully,
    Failed
}

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

    [BsonElement("isManualUpload")]
    [JsonPropertyName("isManualUpload")]
    public bool IsManualUpload { get; set; } = false; // Default to false

    [BsonElement("processingStatus")]
    [BsonRepresentation(BsonType.String)] // Store enum as string for readability in DB
    [JsonPropertyName("processingStatus")]
    [BsonIgnoreIfNull] // Only store if it's relevant (e.g., for manual uploads)
    public ProcessingStatus? Status { get; set; }

    [BsonElement("localFilePath")]
    [JsonPropertyName("localFilePath")]
    [BsonIgnoreIfNull] // Store only while the original uploaded file is needed for processing
    public string? LocalFilePath { get; set; } // Path to the originally uploaded file on the server

    // You might also consider adding:
    public DateTime? UploadedAt { get; set; }
    public string? ProcessingErrorMessage { get; set; } // If Status is Failed

    // Optional: Store a hash of the file for deduplication or integrity checks
    [BsonElement("fileHash")]
    [JsonPropertyName("fileHash")]
    [BsonIgnoreIfNull]
    // [BsonIndexKeys(new string[] { nameof(FileHash) })] // <<<< REMOVE THIS LINE
    public string? FileHash { get; set; }

}
