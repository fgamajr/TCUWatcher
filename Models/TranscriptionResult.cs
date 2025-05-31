using System.Collections.Generic;

namespace TCUWatcher.API.Models
{
    public class TranscriptionWord
    {
        public string? Word { get; set; }
        public float Start { get; set; } // Start time in seconds
        public float End { get; set; }   // End time in seconds
    }

    public class TranscriptionSegment
    {
        public int Id { get; set; }
        public float Seek { get; set; }
        public float Start { get; set; }
        public float End { get; set; }
        public string? Text { get; set; }
        public List<int>? Tokens { get; set; }
        public float Temperature { get; set; }
        public float AvgLogprob { get; set; }
        public float CompressionRatio { get; set; }
        public float NoSpeechProb { get; set; }
        public List<TranscriptionWord>? Words { get; set; } // If getting word timestamps
    }

    public class TranscriptionResult
    {
        public string? Text { get; set; } // Full concatenated text
        public string? Language { get; set; }
        public float Duration { get; set; }
        public List<TranscriptionSegment>? Segments { get; set; } // If using verbose_json
        // Add other fields from OpenAI's verbose JSON response as needed
    }
}