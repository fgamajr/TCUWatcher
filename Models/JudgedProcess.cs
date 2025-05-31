using MongoDB.Bson.Serialization.Attributes;
using System;

namespace TCUWatcher.API.Models
{
    public class JudgedProcessInfo
    {
        [BsonElement("processNumber")]
        public string ProcessNumber { get; set; } = string.Empty;

        [BsonElement("startTimeInVideo")] // TimeSpan from video start
        public TimeSpan? StartTimeInVideo { get; set; } 

        [BsonElement("endTimeInVideo")]
        public TimeSpan? EndTimeInVideo { get; set; }

        [BsonElement("transcriptionSnippet")]
        public string? TranscriptionSnippet { get; set; } 
    }
}