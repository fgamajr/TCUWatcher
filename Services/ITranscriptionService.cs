using System.Threading.Tasks;
using TCUWatcher.API.Models; // For TranscriptionResult

namespace TCUWatcher.API.Services
{
    public interface ITranscriptionService
    {
        /// <summary>
        /// Transcribes the audio file at the given path.
        /// </summary>
        /// <param name="audioFilePath">The local path to the audio file.</param>
        /// <param name="liveEventIdForLogging">The ID of the live event for logging context.</param>
        /// <param name="languageCode">Optional ISO 639-1 language code (e.g., "pt" for Portuguese).</param>
        /// <returns>A TranscriptionResult object or null if transcription failed.</returns>
        Task<TranscriptionResult?> TranscribeAudioAsync(string audioFilePath, string liveEventIdForLogging, string? languageCode = "pt");
    }
}