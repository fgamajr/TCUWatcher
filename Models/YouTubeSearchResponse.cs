using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace TCUWatcher.API.Models;

public class YouTubeSearchResponse
{
    [JsonPropertyName("items")]
    public List<YouTubeSearchItem> Items { get; set; } = new();
}
