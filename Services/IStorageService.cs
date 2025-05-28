// In TCUWatcher.API.Services (or TCUWatcher.API.Services.Storage)
public interface IStorageService
{
    /// <summary>
    /// Saves a snapshot.
    /// </summary>
    /// <param name="snapshotData">The image data as a byte array.</param>
    /// <param name="liveEventId">The identifier of the LiveEvent this snapshot belongs to (e.g., your MongoDB document ID or VideoId).</param>
    /// <param name="timestamp">A timestamp for the snapshot (e.g., DateTime.UtcNow).</param>
    /// <param name="fileExtension">The desired file extension (e.g., "jpg", "png").</param>
    /// <returns>A string identifier for the stored snapshot (e.g., a file path or a cloud storage URI), or null if saving failed.</returns>
    Task<string?> SaveSnapshotAsync(byte[] snapshotData, string liveEventId, DateTime timestamp, string fileExtension);

    // Alternative using Stream
    // Task<string?> SaveSnapshotAsync(Stream snapshotStream, string liveEventId, DateTime timestamp, string fileExtension);
}