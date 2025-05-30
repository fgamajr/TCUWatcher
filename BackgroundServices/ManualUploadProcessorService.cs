// In BackgroundServices/ManualUploadProcessorService.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO; // For Path
using System.Linq;
using System.Text; // For ffprobe output parsing
using System.Diagnostics; // For Process
using System.Globalization; // For double.Parse
using System.Threading;
using System.Threading.Tasks;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;
using MongoDB.Driver; // For Builders

namespace TCUWatcher.API.BackgroundServices
{
    public class ManualUploadProcessorService : BackgroundService
    {
        private readonly ILogger<ManualUploadProcessorService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _checkPendingUploadsInterval;
        private readonly TimeSpan _snapshotIntervalFromFile;
        private readonly string _snapshotFileExtension;
        private readonly string _ffmpegPath; // Needed for ffprobe

        public ManualUploadProcessorService(
            ILogger<ManualUploadProcessorService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _checkPendingUploadsInterval = TimeSpan.FromSeconds(configuration.GetValue<int?>("ManualUploadProcessing:CheckIntervalSeconds") ?? 60);
            _snapshotIntervalFromFile = TimeSpan.FromSeconds(configuration.GetValue<int?>("Snapshotting:SnapshotIntervalSeconds") ?? 10); // Reuse snapshot interval
            _snapshotFileExtension = configuration["Photographer:Format"]?.ToLowerInvariant() ?? "png";
            if (_snapshotFileExtension == "mjpeg") _snapshotFileExtension = "jpg";
            _ffmpegPath = configuration["Photographer:FfmpegPath"] ?? "ffmpeg"; // Assuming ffprobe is with ffmpeg
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ManualUploadProcessorService is starting.");
            stoppingToken.Register(() => _logger.LogInformation("ManualUploadProcessorService is stopping."));

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("ManualUploadProcessorService: Checking for pending manual uploads.");
                try
                {
                    await ProcessPendingUploadsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                     _logger.LogInformation("ManualUploadProcessorService processing loop canceled.");
                     break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ManualUploadProcessorService main loop.");
                }
                await Task.Delay(_checkPendingUploadsInterval, stoppingToken);
            }
            _logger.LogInformation("ManualUploadProcessorService has stopped.");
        }

        private async Task ProcessPendingUploadsAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
            var photographerService = scope.ServiceProvider.GetRequiredService<IPhotographerService>();
            var storageService = scope.ServiceProvider.GetRequiredService<IStorageService>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>(); // For base storage path

            var filter = Builders<LiveEvent>.Filter.Eq(e => e.IsManualUpload, true) &
                         Builders<LiveEvent>.Filter.Eq(e => e.Status, ProcessingStatus.Pending);
            var pendingUploads = await mongoService.FindAsync(filter);

            if (!pendingUploads.Any())
            {
                _logger.LogTrace("No pending manual uploads found.");
                return;
            }

            _logger.LogInformation("Found {Count} pending manual uploads to process.", pendingUploads.Count);

            foreach (var liveEvent in pendingUploads)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(liveEvent.LocalFilePath) || !System.IO.File.Exists(liveEvent.LocalFilePath))
                {
                    _logger.LogError("Local file path for manual upload {VideoId} is missing or file does not exist: {Path}. Setting to Failed.", liveEvent.VideoId, liveEvent.LocalFilePath);
                    await UpdateEventStatusAsync(mongoService, liveEvent.Id!, ProcessingStatus.Failed, "Local file missing");
                    continue;
                }

                _logger.LogInformation("Processing manual upload for VideoId: {VideoId}, File: {FilePath}", liveEvent.VideoId, liveEvent.LocalFilePath);
                await UpdateEventStatusAsync(mongoService, liveEvent.Id!, ProcessingStatus.Processing);

                try
                {
                    TimeSpan? videoDuration = await GetVideoDurationAsync(liveEvent.LocalFilePath, liveEvent.VideoId!);
                    if (!videoDuration.HasValue)
                    {
                        await UpdateEventStatusAsync(mongoService, liveEvent.Id!, ProcessingStatus.Failed, "Could not get video duration.");
                        continue;
                    }
                    _logger.LogInformation("Video {VideoId} duration: {Duration}", liveEvent.VideoId, videoDuration.Value);

                    // Snapshotting
                    TimeSpan currentTimeOffset = TimeSpan.Zero;
                    while (currentTimeOffset < videoDuration.Value && !stoppingToken.IsCancellationRequested)
                    {
                        byte[]? snapshotData = await photographerService.TakeSnapshotFromFileAsync(liveEvent.LocalFilePath, liveEvent.VideoId!, currentTimeOffset);
                        if (snapshotData != null && snapshotData.Length > 0)
                        {
                            // Use UtcNow for file naming to ensure uniqueness and sort order, metadata could store relative time
                            await storageService.SaveSnapshotAsync(snapshotData, liveEvent.VideoId!, DateTime.UtcNow, _snapshotFileExtension);
                        }
                        else {
                            _logger.LogWarning("Failed to get snapshot for {VideoId} at offset {Offset}", liveEvent.VideoId, currentTimeOffset);
                        }
                        currentTimeOffset += _snapshotIntervalFromFile;
                    }
                    _logger.LogInformation("Finished snapshotting for {VideoId}", liveEvent.VideoId);
                    
                    // Audio Extraction
                    string baseStoragePath = configuration["Storage:Local:BasePath"] ?? "TCU_Snapshots";
                    string eventSnapshotPath = Path.Combine(baseStoragePath, liveEvent.VideoId!); // Path where snapshots are stored
                    string audioExtension = "m4a"; // Example, could be configurable
                    string? extractedAudioPath = await photographerService.ExtractFullAudioAsync(liveEvent.LocalFilePath, liveEvent.VideoId!, eventSnapshotPath, audioExtension);

                    if (string.IsNullOrEmpty(extractedAudioPath))
                    {
                         _logger.LogWarning("Full audio extraction failed or returned no path for {VideoId}", liveEvent.VideoId);
                         // Decide if this is a critical failure for the whole processing
                    } else {
                         _logger.LogInformation("Full audio extracted for {VideoId} to {Path}", liveEvent.VideoId, extractedAudioPath);
                         // Optionally store 'extractedAudioPath' in the LiveEvent document
                    }


                    if (stoppingToken.IsCancellationRequested) {
                        await UpdateEventStatusAsync(mongoService, liveEvent.Id!, ProcessingStatus.Pending, "Processing cancelled."); // Revert to Pending or set to Cancelled
                        _logger.LogInformation("Processing cancelled for {VideoId}", liveEvent.VideoId);
                        continue;
                    }

                    await UpdateEventStatusAsync(mongoService, liveEvent.Id!, ProcessingStatus.CompletedSuccessfully);
                    _logger.LogInformation("Successfully processed manual upload for VideoId: {VideoId}", liveEvent.VideoId);

                    // Optionally delete the original uploaded file from staging
                    // if (System.IO.File.Exists(liveEvent.LocalFilePath))
                    // {
                    //     _logger.LogInformation("Deleting original uploaded file: {FilePath}", liveEvent.LocalFilePath);
                    //     System.IO.File.Delete(liveEvent.LocalFilePath);
                    //     await mongoService.UpdateLiveEventAsync(liveEvent.Id, Builders<LiveEvent>.Update.Unset(e => e.LocalFilePath));
                    // }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing manual upload for VideoId: {VideoId}", liveEvent.VideoId);
                    await UpdateEventStatusAsync(mongoService, liveEvent.Id!, ProcessingStatus.Failed, ex.Message);
                }
            }
        }

        private async Task<TimeSpan?> GetVideoDurationAsync(string filePath, string videoIdForLogging)
        {
            // Use ffprobe to get duration: ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "input.mp4"
            string ffprobePath = _ffmpegPath.Replace("ffmpeg", "ffprobe"); // Basic assumption
            if (!System.IO.File.Exists(ffprobePath)) { // Fallback if ffprobe isn't found this way
                 _logger.LogWarning("ffprobe not found at assumed path {ffprobePath}, trying 'ffprobe' directly from PATH.", ffprobePath);
                 ffprobePath = "ffprobe"; 
            }

            string args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"";
            _logger.LogDebug("Getting duration for {VideoId} with ffprobe args: {Args}", videoIdForLogging, args);

            ProcessStartInfo psi = new ProcessStartInfo(ffprobePath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var process = new Process { StartInfo = psi };
                StringBuilder outputBuilder = new StringBuilder();
                StringBuilder errorBuilder = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if(e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if(e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = await Task.Run(() => process.WaitForExit(10000)); // 10 sec timeout for ffprobe

                if (!exited) {
                    process.Kill(true);
                    _logger.LogError("ffprobe timed out getting duration for {VideoId}", videoIdForLogging);
                    return null;
                }
                if (process.ExitCode != 0) {
                    _logger.LogError("ffprobe failed for {VideoId}. ExitCode: {ExitCode}. Errors: {Errors}", videoIdForLogging, process.ExitCode, errorBuilder.ToString());
                    return null;
                }
                
                string durationStr = outputBuilder.ToString().Trim();
                if (double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
                _logger.LogError("Could not parse ffprobe duration output for {VideoId}: '{Output}'", videoIdForLogging, durationStr);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception getting video duration for {VideoId}", videoIdForLogging);
                return null;
            }
        }
        
        private async Task UpdateEventStatusAsync(IMongoService mongoService, string eventId, ProcessingStatus status, string? errorMessage = null)
        {
            var updateBuilder = Builders<LiveEvent>.Update.Set(e => e.Status, status);
            // if (errorMessage != null) {
            //     updateBuilder = updateBuilder.Set(e => e.ProcessingErrorMessage, errorMessage);
            // }
            await mongoService.UpdateLiveEventAsync(eventId, updateBuilder);
        }
    }
}