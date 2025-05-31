using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Globalization; // For CultureInfo in GetSnapshotOffsetFromPath
using System.IO;
using System.Linq;
using System.Text; // For StringBuilder
using System.Threading;
using System.Threading.Tasks;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;

namespace TCUWatcher.API.BackgroundServices
{
    public class SessionSummarizationService : BackgroundService
    {
        private readonly ILogger<SessionSummarizationService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _checkInterval;
        private readonly int _maxSummarizationRetries;
        private readonly TimeSpan _processingTimeout;

        public SessionSummarizationService(
            ILogger<SessionSummarizationService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _checkInterval = TimeSpan.FromMinutes(configuration.GetValue<int?>("SessionSummarization:CheckIntervalMinutes") ?? 5);
            _maxSummarizationRetries = configuration.GetValue<int?>("SessionSummarization:MaxRetries") ?? 3;
            _processingTimeout = TimeSpan.FromHours(configuration.GetValue<int?>("SessionSummarization:ProcessingTimeoutHours") ?? 2);

            _logger.LogInformation(
                "SessionSummarizationService configured. CheckInterval: {CheckInterval}, MaxRetries: {MaxRetries}, ProcessingTimeout: {ProcessingTimeout}",
                _checkInterval, _maxSummarizationRetries, _processingTimeout);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("SessionSummarizationService - ExecuteAsync ENTERED.");
            stoppingToken.Register(() => _logger.LogInformation("SessionSummarizationService is stopping (triggered by CancellationToken)."));

            try
            {
                // Optional: Initial delay before the first run
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); 
            }
            catch (OperationCanceledException) { _logger.LogInformation("Initial delay canceled for SessionSummarizationService."); return; }


            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("SessionSummarizationService - TOP OF WHILE LOOP.");
                try
                {
                    _logger.LogDebug("SessionSummarizationService: Calling ProcessNextSummarizableEventAsync.");
                    await ProcessNextSummarizableEventAsync(stoppingToken);
                    
                    _logger.LogDebug("SessionSummarizationService: Calling HandleStuckProcessingItemsAsync.");
                    await HandleStuckProcessingItemsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("SessionSummarizationService main loop explicitly canceled during an operation.");
                    break; 
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in SessionSummarizationService main cycle. Will retry after delay.");
                }

                _logger.LogDebug("SessionSummarizationService - End of try-catch in while loop. Delaying for {CheckInterval}", _checkInterval);
                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("SessionSummarizationService main loop delay canceled. Exiting.");
                    break; 
                }
            }
            _logger.LogInformation("SessionSummarizationService - ExecuteAsync EXITED.");
        }

        private async Task ProcessNextSummarizableEventAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ProcessNextSummarizableEventAsync: Method ENTERED.");
            LiveEvent? liveEventToProcess = null; 

            using var scope = _scopeFactory.CreateScope();
            var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
            _logger.LogInformation("ProcessNextSummarizableEventAsync: IMongoService resolved.");
            var ocrService = scope.ServiceProvider.GetRequiredService<IOcrProcessExtractorService>();
            _logger.LogInformation("ProcessNextSummarizableEventAsync: IOcrProcessExtractorService resolved.");
            var transcriptionService = scope.ServiceProvider.GetRequiredService<ITranscriptionService>();
            _logger.LogInformation("ProcessNextSummarizableEventAsync: ITranscriptionService resolved.");
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            _logger.LogInformation("ProcessNextSummarizableEventAsync: All services resolved.");

            _logger.LogInformation("ProcessNextSummarizableEventAsync: Attempting to claim event from DB.");
            liveEventToProcess = await ClaimNextPendingSummaryEventAsync(mongoService);
            _logger.LogInformation("ProcessNextSummarizableEventAsync: ClaimNextPendingSummaryEventAsync returned. Event ID: {ClaimedEventId}", liveEventToProcess?.Id ?? "NONE");

            if (liveEventToProcess == null || string.IsNullOrEmpty(liveEventToProcess.Id))
            {
                return;
            }
            
            // liveEventToProcess.SummaryRetryCount is incremented by ClaimNext... on successful claim.
            _logger.LogInformation("Starting summarization for LiveEvent: {LiveEventId} (VideoId: {VideoId}), Attempt: {AttemptCount}", 
                liveEventToProcess.Id, liveEventToProcess.VideoId, liveEventToProcess.SummaryRetryCount ?? 1);

            string fullTranscriptionText = "Audio not transcribed or not available.";
            var judgedProcesses = new List<JudgedProcessInfo>();
            bool ocrFoundAnyProcesses = false;
            bool transcriptionSucceeded = false;
            List<string> snapshotPaths = new List<string>();

            try
            {
                // --- A. Get Snapshot Paths ---
                string baseSnapshotPath = configuration["Storage:Local:BasePath"] ?? "TCU_Snapshots";
                string eventSnapshotDirectory = Path.Combine(baseSnapshotPath, liveEventToProcess.VideoId!);
                if (Directory.Exists(eventSnapshotDirectory))
                {
                    string snapshotExtension = (configuration["Photographer:Format"]?.ToLowerInvariant() ?? "png");
                    if (snapshotExtension == "mjpeg") snapshotExtension = "jpg"; // Ensure correct extension for jpg
                    snapshotPaths = Directory.GetFiles(eventSnapshotDirectory, $"*.{snapshotExtension}").OrderBy(f => f).ToList();
                }
                _logger.LogInformation("Found {Count} snapshots for event {VideoId}", snapshotPaths.Count, liveEventToProcess.VideoId);

                // --- B. OCR Snapshots for Process Numbers ---
                var ocrProcessNumberOccurrences = new Dictionary<string, List<TimeSpan>>();
                if (snapshotPaths.Any()) {
                    _logger.LogInformation("Starting OCR for {SnapshotCount} snapshots for event {VideoId}.", snapshotPaths.Count, liveEventToProcess.Id);
                    int processedSnapshotsCount = 0;
                    foreach (var snapshotPath in snapshotPaths)
                    {
                        if (stoppingToken.IsCancellationRequested) throw new OperationCanceledException("Summarization cancelled during OCR phase.");
                        _logger.LogDebug("Calling OCR for snapshot {SnapshotPath} ({ProcessedCount}/{TotalCount}).", snapshotPath, ++processedSnapshotsCount, snapshotPaths.Count);
                        List<string> numbersFromFrame = await ocrService.ExtractValidatedProcessNumbersAsync(snapshotPath, liveEventToProcess.Id);
                        foreach (var procNum in numbersFromFrame)
                        {
                            TimeSpan snapshotOffset = GetSnapshotOffsetFromPath(snapshotPath, liveEventToProcess.StartedAt, liveEventToProcess.IsManualUpload, liveEventToProcess.UploadedAt);
                            if (!ocrProcessNumberOccurrences.ContainsKey(procNum))
                            {
                                ocrProcessNumberOccurrences[procNum] = new List<TimeSpan>();
                            }
                            ocrProcessNumberOccurrences[procNum].Add(snapshotOffset);
                        }
                    }
                    _logger.LogInformation("OCR completed for event {VideoId}. Unique process numbers found: {Count}", liveEventToProcess.VideoId, ocrProcessNumberOccurrences.Count);
                    if (ocrProcessNumberOccurrences.Any()) ocrFoundAnyProcesses = true;
                } else {
                    _logger.LogInformation("No snapshots found to OCR for event {VideoId}", liveEventToProcess.VideoId);
                }

                // --- C. Transcribe Full Audio ---
                _logger.LogInformation("Attempting transcription for event {VideoId}.", liveEventToProcess.Id);
                string audioFileExtension = "m4a"; // Make configurable if necessary
                string audioFileName = $"audio_{liveEventToProcess.VideoId}.{audioFileExtension}";
                string audioFilePath = Path.Combine(eventSnapshotDirectory, audioFileName); // Assumes audio is in the same eventSnapshotDirectory
                TranscriptionResult? transcriptionResult = null;

                if (File.Exists(audioFilePath))
                {
                    transcriptionResult = await transcriptionService.TranscribeAudioAsync(audioFilePath, liveEventToProcess.Id);
                    if (transcriptionResult != null && !string.IsNullOrWhiteSpace(transcriptionResult.Text)) {
                        fullTranscriptionText = transcriptionResult.Text;
                        transcriptionSucceeded = true;
                        _logger.LogInformation("Transcription successful for event {VideoId}. Text length: {Length}", liveEventToProcess.Id, fullTranscriptionText.Length);
                    } else {
                        _logger.LogWarning("Transcription service returned null or empty text for {AudioFilePath} for event {EventId}", audioFilePath, liveEventToProcess.Id);
                        fullTranscriptionText = "Transcription failed or returned empty.";
                    }
                }
                else
                {
                    _logger.LogWarning("Audio file {AudioFilePath} not found for event {VideoId}. Full transcription will be skipped.", audioFilePath, liveEventToProcess.VideoId);
                    fullTranscriptionText = "Audio file not found.";
                }
                
                // --- D. Segmentation & Per-Process Transcription ---
                if (ocrProcessNumberOccurrences.Any() && transcriptionSucceeded && transcriptionResult?.Segments != null) {
                    _logger.LogInformation("Starting segmentation for {EventId} based on {OcrCount} OCR'd process numbers and transcription.", liveEventToProcess.Id, ocrProcessNumberOccurrences.Count);
                    foreach (var entry in ocrProcessNumberOccurrences)
                    {
                        if (stoppingToken.IsCancellationRequested) throw new OperationCanceledException("Summarization cancelled during segmentation phase.");
                        string processNumber = entry.Key;
                        var timestamps = entry.Value.OrderBy(t => t).ToList();
                        if (!timestamps.Any()) continue;

                        var judgedProcess = new JudgedProcessInfo
                        {
                            ProcessNumber = processNumber,
                            StartTimeInVideo = timestamps.First(),
                            EndTimeInVideo = timestamps.Last() 
                        };
                        judgedProcess.TranscriptionSnippet = ExtractSnippetFromTranscription(transcriptionResult, judgedProcess.StartTimeInVideo.Value, judgedProcess.EndTimeInVideo.Value);
                        judgedProcesses.Add(judgedProcess);
                    }
                    _logger.LogInformation("Segmentation completed. Identified {Count} judged processes for event {VideoId}.", judgedProcesses.Count, liveEventToProcess.VideoId);
                } else {
                    _logger.LogInformation("Segmentation skipped for event {VideoId}: OCR found no processes OR transcription failed/unavailable OR no segments in transcription.", liveEventToProcess.VideoId);
                }

                // --- E. Determine Final Summary Status and Update MongoDB ---
                ProcessingStatus finalSummaryStatus;
                string? finalErrorMessage = null;

                if (transcriptionSucceeded && (ocrFoundAnyProcesses || !snapshotPaths.Any()))
                {
                    finalSummaryStatus = ProcessingStatus.CompletedSuccessfully;
                    if (!ocrFoundAnyProcesses && snapshotPaths.Any()) {
                        finalErrorMessage = "Transcription successful, but no process numbers were extracted from snapshots.";
                         _logger.LogWarning("Summarization for LiveEvent {LiveEventId}: {ErrorMessage}", liveEventToProcess.Id, finalErrorMessage);
                    } else if (!snapshotPaths.Any()) {
                         finalErrorMessage = "Transcription successful. No snapshots were available for OCR.";
                         _logger.LogInformation("Summarization for LiveEvent {LiveEventId}: {ErrorMessage}", liveEventToProcess.Id, finalErrorMessage);
                    } else {
                        _logger.LogInformation("Full summarization successful for LiveEvent: {LiveEventId}", liveEventToProcess.Id);
                    }
                }
                else if (ocrFoundAnyProcesses && !transcriptionSucceeded)
                {
                    finalSummaryStatus = ProcessingStatus.Failed; 
                    finalErrorMessage = "OCR found process numbers, but full audio transcription failed or was unavailable.";
                    _logger.LogWarning("Summarization for LiveEvent {LiveEventId} marked as Failed: {ErrorMessage}", liveEventToProcess.Id, finalErrorMessage);
                }
                else 
                {
                    finalSummaryStatus = ProcessingStatus.Failed; 
                    finalErrorMessage = "Summarization failed: Transcription was unsuccessful/unavailable, AND OCR found no process numbers (or no snapshots to process).";
                    _logger.LogError("Summarization failed to produce a useful summary for LiveEvent {LiveEventId}. Details: {ErrorMessage}", liveEventToProcess.Id, finalErrorMessage);
                }

                var updateDefinition = Builders<LiveEvent>.Update
                    .Set(e => e.FullSessionTranscription, fullTranscriptionText)
                    .Set(e => e.JudgedProcesses, judgedProcesses) 
                    .Set(e => e.SummaryStatus, finalSummaryStatus)
                    .Set(e => e.SummaryErrorMessage, finalErrorMessage) 
                    .Set(e => e.SummaryRetryCount, liveEventToProcess.SummaryRetryCount); // Retry count was already incremented on claim or by failure handler

                await mongoService.UpdateLiveEventAsync(liveEventToProcess.Id, updateDefinition);
                _logger.LogInformation("Summarization attempt finalized for LiveEvent: {LiveEventId} with status {FinalStatus}", liveEventToProcess.Id, finalSummaryStatus);

            } // End of main try block for processing an item
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Summarization for LiveEvent {LiveEventId} was canceled by application shutdown. Attempting to reset to Pending.", liveEventToProcess.Id);
                if (liveEventToProcess.Id != null)
                {
                    var currentEventState = await mongoService.GetLiveEventByIdAsync(liveEventToProcess.Id);
                    if(currentEventState != null && currentEventState.SummaryStatus == ProcessingStatus.Processing)
                    {
                        var cancelUpdate = Builders<LiveEvent>.Update
                            .Set(e => e.SummaryStatus, ProcessingStatus.Pending);
                            // Do not increment retry count here as it was an external cancellation, not a processing failure.
                        await mongoService.UpdateLiveEventAsync(liveEventToProcess.Id, cancelUpdate);
                         _logger.LogInformation("LiveEvent {LiveEventId} summarization status reset to Pending due to cancellation.", liveEventToProcess.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during summarization of LiveEvent: {LiveEventId} (VideoId: {VideoId})", liveEventToProcess.Id, liveEventToProcess.VideoId);
                if (liveEventToProcess.Id != null)
                {
                    // Retry count was already incremented when claiming.
                    // If error happens after claiming & incrementing, check against max retries.
                    int currentAttemptCount = liveEventToProcess.SummaryRetryCount ?? 1; 
                    ProcessingStatus nextStatus = (currentAttemptCount < _maxSummarizationRetries) ? ProcessingStatus.Pending : ProcessingStatus.Failed;
                    
                    var failureUpdate = Builders<LiveEvent>.Update
                        .Set(e => e.SummaryStatus, nextStatus)
                        .Set(e => e.SummaryErrorMessage, $"Summarization failed: {ex.Message}")
                        .Set(e => e.SummaryRetryCount, currentAttemptCount); // Keep the already incremented count
                    await mongoService.UpdateLiveEventAsync(liveEventToProcess.Id, failureUpdate);
                    _logger.LogWarning("Summarization for LiveEvent {LiveEventId} failed. Status set to {NextStatus}. Attempt {AttemptCount}/{MaxRetries}", 
                        liveEventToProcess.Id, nextStatus, currentAttemptCount, _maxSummarizationRetries);
                }
            }
        }
        
        private async Task<LiveEvent?> ClaimNextPendingSummaryEventAsync(IMongoService mongoService)
        {
            var filter = Builders<LiveEvent>.Filter.And(
                Builders<LiveEvent>.Filter.Eq(e => e.Status, ProcessingStatus.CompletedSuccessfully), 
                Builders<LiveEvent>.Filter.Eq(e => e.IsTitleProcessed, true),                         
                Builders<LiveEvent>.Filter.Or(
                    Builders<LiveEvent>.Filter.Eq(e => e.SummaryStatus, ProcessingStatus.Pending),
                    Builders<LiveEvent>.Filter.Eq(e => e.SummaryStatus, null) 
                ),
                Builders<LiveEvent>.Filter.Or(
                    Builders<LiveEvent>.Filter.Lt(e => e.SummaryRetryCount, _maxSummarizationRetries),
                    Builders<LiveEvent>.Filter.Eq(e => e.SummaryRetryCount, null)
                )
            );

            var update = Builders<LiveEvent>.Update
                .Set(e => e.SummaryStatus, ProcessingStatus.Processing)
                .Set(e => e.SummaryProcessingStartedAt, DateTime.UtcNow)
                .Inc(e => e.SummaryRetryCount, 1); // Increment retry count as we are starting an attempt. Mongo handles $inc on null as $inc on 0.

            var options = new FindOneAndUpdateOptions<LiveEvent, LiveEvent> 
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<LiveEvent>.Sort.Ascending(e => e.UploadedAt) 
            };
            
            // This method must exist in IMongoService and MongoService
            return await mongoService.FindOneAndUpdateLiveEventAsync(filter, update, options); 
        }

        private async Task HandleStuckProcessingItemsAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
            _logger.LogDebug("Checking for summarization tasks stuck in 'Processing' state...");

            var cutoffTime = DateTime.UtcNow.Subtract(_processingTimeout);
            var stuckFilter = Builders<LiveEvent>.Filter.And(
                Builders<LiveEvent>.Filter.Eq(e => e.SummaryStatus, ProcessingStatus.Processing),
                Builders<LiveEvent>.Filter.Lt(e => e.SummaryProcessingStartedAt, cutoffTime)
            );

            var stuckEvents = await mongoService.FindAsync(stuckFilter); 
            if (stuckEvents != null && stuckEvents.Any())
            {
                foreach (var stuckEvent in stuckEvents)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    _logger.LogWarning("LiveEvent {LiveEventId} (VideoId: {VideoId}) found stuck in summarization processing (started at {StartTime}, timeout {Timeout}). Resetting to Pending.",
                        stuckEvent.Id, stuckEvent.VideoId, stuckEvent.SummaryProcessingStartedAt, _processingTimeout);

                    // Increment retry count for a stuck item as well, as this processing attempt failed by timeout.
                    var currentRetryCount = (stuckEvent.SummaryRetryCount ?? 0) + 1;
                    ProcessingStatus nextStatus = (currentRetryCount <= _maxSummarizationRetries) ? ProcessingStatus.Pending : ProcessingStatus.Failed;

                    var resetUpdate = Builders<LiveEvent>.Update
                        .Set(e => e.SummaryStatus, nextStatus)
                        .Set(e => e.SummaryRetryCount, currentRetryCount) 
                        .Set(e => e.SummaryErrorMessage, $"Processing timed out after {_processingTimeout} and was reset to {nextStatus}.");
                    
                    if (stuckEvent.Id != null) await mongoService.UpdateLiveEventAsync(stuckEvent.Id, resetUpdate);
                }
            }
            else
            {
                _logger.LogTrace("No summarization tasks found stuck in processing.");
            }
        }

        // This method needs careful implementation based on how snapshot filenames store time information
        // relative to the video's start. For manual uploads, it's tricky if only absolute save times are used.
        private TimeSpan GetSnapshotOffsetFromPath(string snapshotPath, DateTime videoNominalStartTime, bool isManualUpload, DateTime? uploadedAt)
        {
            // Placeholder: This logic needs to be robust.
            // If snapshots are named like "offset_HHMMSSFFF.png" or if ManualUploadProcessorService
            // stored {Path, Offset} metadata, that would be ideal.
            // For now, assuming filenames are "yyyyMMdd_HHmmss_fff.png" (absolute creation time)
            try
            {
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(snapshotPath);
                if (DateTime.TryParseExact(fileNameWithoutExt, "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime snapshotCreationTime))
                {
                    DateTime referenceTimeForManual = uploadedAt ?? videoNominalStartTime; // For manual, use upload time as rough start if StartedAt is not video start
                    DateTime referenceTime = isManualUpload ? referenceTimeForManual : videoNominalStartTime;
                    
                    TimeSpan offset = snapshotCreationTime.ToUniversalTime() - referenceTime.ToUniversalTime();
                    return offset < TimeSpan.Zero ? TimeSpan.Zero : offset;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not parse offset from snapshot path: {SnapshotPath}", snapshotPath); }
            _logger.LogWarning("Returning TimeSpan.Zero as offset for snapshot: {SnapshotPath}. Accurate segmentation may be affected.", snapshotPath);
            return TimeSpan.Zero; 
        }

        private string ExtractSnippetFromTranscription(TranscriptionResult transcription, TimeSpan snippetStartTime, TimeSpan snippetEndTime)
        {
            if (transcription?.Segments == null || !transcription.Segments.Any() || snippetStartTime >= snippetEndTime) 
                return string.Empty;

            var snippetBuilder = new StringBuilder();
            foreach(var segment in transcription.Segments.OrderBy(s => s.Start))
            {
                // Check if segment overlaps with the desired [snippetStartTime, snippetEndTime]
                double segmentStart = segment.Start;
                double segmentEnd = segment.End;
                double desiredStart = snippetStartTime.TotalSeconds;
                double desiredEnd = snippetEndTime.TotalSeconds;

                // If segment is entirely within the desired range, or overlaps
                if (segmentStart < desiredEnd && segmentEnd > desiredStart)
                {
                    // For more precision, if segments have word-level timestamps,
                    // you could iterate words and include only those within the precise start/end.
                    // For now, including full text of overlapping segments.
                    // This could be refined to trim words that are partially outside the range.
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                         snippetBuilder.Append(segment.Text.Trim() + " ");
                    }
                }
                if (segmentStart >= desiredEnd) // Segments are ordered by start, so we can break early
                {
                    break;
                }
            }
            return snippetBuilder.ToString().Trim();
        }
    }
}