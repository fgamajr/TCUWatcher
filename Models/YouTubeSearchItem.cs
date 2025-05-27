using System.Text.Json.Serialization;

namespace TCUWatcher.API.Models;

public class YouTubeSearchItem
{
    [JsonPropertyName("id")]
    public VideoIdInfo Id { get; set; } = new();

    [JsonPropertyName("snippet")]
    public SnippetInfo Snippet { get; set; } = new();
}

public class VideoIdInfo
{
    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;
}

public class SnippetInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = string.Empty;
}
