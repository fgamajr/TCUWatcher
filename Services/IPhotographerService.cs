// In TCUWatcher.API.Services (or a new sub-namespace like TCUWatcher.API.Services.Snapshots)
public interface IPhotographerService
{
    /// <summary>
    /// Takes a snapshot from the given video URL.
    /// </summary>
    /// <param name="videoUrl">The publicly accessible URL of the video stream (e.g., YouTube watch URL).</param>
    /// <param name="liveEventId">The ID of the live event for context/logging.</param>
    /// <returns>A byte array containing the snapshot image data (e.g., JPG or PNG), or null if capture failed.</returns>
    Task<byte[]?> TakeSnapshotAsync(string videoUrl, string liveEventId);

    // We could also consider returning a Stream if memory usage is a concern for large images,
    // but byte[] might be simpler for inter-process communication if using external tools.
    // Task<Stream?> TakeSnapshotAsStreamAsync(string videoUrl, string liveEventId);

    // In IPhotographerService.cs
    Task<byte[]?> TakeSnapshotFromFileAsync(string localFilePath, string liveEventId, TimeSpan timeOffset);
    Task<string?> ExtractFullAudioAsync(string localFilePath, string liveEventId, string outputAudioDirectory, string fileExtension); // Returns path to audio file or null
}