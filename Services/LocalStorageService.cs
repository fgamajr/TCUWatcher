using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services
{
        public class LocalStorageService : IStorageService
    {
        private readonly ILogger<LocalStorageService> _logger;
        private readonly string _baseStoragePath; // Non-nullable field

        public LocalStorageService(IConfiguration configuration, ILogger<LocalStorageService> logger)
        {
            _logger = logger;
            string? configuredPath = configuration["Storage:Local:BasePath"]; // Read as a nullable string

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                _logger.LogError("Base storage path 'Storage:Local:BasePath' is not configured in appsettings.json or other configuration sources.");
                // Assign a non-null fallback path
                _baseStoragePath = Path.Combine(AppContext.BaseDirectory, "snapshots_output"); 
                _logger.LogWarning("Using default base storage path: {DefaultPath}", _baseStoragePath);
            }
            else
            {
                _baseStoragePath = configuredPath; // Now we know configuredPath is not null or whitespace
            }
            
            // Ensure the base directory exists
            _logger.LogInformation("Configured base storage path: {BasePath}", _baseStoragePath);
            
            // Ensure the base directory exists
            try
            {
                Directory.CreateDirectory(_baseStoragePath);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to create base storage directory: {BasePath}. Snapshots may not be saved.", _baseStoragePath);
                // Depending on desired behavior, you might want to throw here to prevent the app from starting
                // if local storage is critical and misconfigured/unwritable.
            }
        }

        public async Task<string?> SaveSnapshotAsync(byte[] snapshotData, string liveEventId, DateTime timestamp, string fileExtension)
        {
            if (string.IsNullOrWhiteSpace(_baseStoragePath))
            {
                _logger.LogError("Base storage path is not configured or invalid. Cannot save snapshot for LiveEvent: {LiveEventId}", liveEventId);
                return null;
            }

            if (snapshotData == null || snapshotData.Length == 0)
            {
                _logger.LogWarning("Snapshot data is null or empty for LiveEvent: {LiveEventId}. Cannot save.", liveEventId);
                return null;
            }

            try
            {
                // Sanitize liveEventId if it's used directly in path to avoid issues with certain characters.
                // For simplicity here, assuming liveEventId is safe or using a hash if it's complex.
                string eventSpecificPath = Path.Combine(_baseStoragePath, liveEventId);
                Directory.CreateDirectory(eventSpecificPath); // Ensure the event-specific directory exists

                string fileName = $"{timestamp:yyyyMMdd_HHmmss_fff}.{fileExtension.TrimStart('.')}";
                string filePath = Path.Combine(eventSpecificPath, fileName);

                await File.WriteAllBytesAsync(filePath, snapshotData);
                _logger.LogInformation("Snapshot saved for LiveEvent {LiveEventId} to {FilePath}", liveEventId, filePath);
                return filePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save snapshot for LiveEvent {LiveEventId}.", liveEventId);
                return null;
            }
        }
    }
}