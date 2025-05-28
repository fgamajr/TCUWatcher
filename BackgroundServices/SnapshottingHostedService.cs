using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection; // For IServiceScopeFactory
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TCUWatcher.API.Models; // For LiveEvent
using TCUWatcher.API.Services; // For IMongoService, IPhotographerService, IStorageService
using MongoDB.Driver; // For Builders<LiveEvent>.Filter

namespace TCUWatcher.API.BackgroundServices // Assuming you place it here
{
    public class SnapshottingHostedService : BackgroundService
    {
        private readonly ILogger<SnapshottingHostedService> _logger;
        private readonly IServiceScopeFactory _scopeFactory; // To create scopes for scoped services
        private readonly IConfiguration _configuration;

        private readonly TimeSpan _checkActiveLivesInterval;
        private readonly TimeSpan _snapshotIntervalPerEvent;
        private readonly string _snapshotFileExtension;

        // To keep track of active snapshotting tasks, Key: VideoId, Value: CancellationTokenSource for that task
        private readonly Dictionary<string, CancellationTokenSource> _activeSnapshottingProcesses = new();
        private readonly object _lock = new object(); // To synchronize access to _activeSnapshottingProcesses

        public SnapshottingHostedService(
            ILogger<SnapshottingHostedService> logger,
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _configuration = configuration;

            _checkActiveLivesInterval = TimeSpan.FromSeconds(configuration.GetValue<int?>("Snapshotting:CheckActiveLivesIntervalSeconds") ?? 60);
            _snapshotIntervalPerEvent = TimeSpan.FromSeconds(configuration.GetValue<int?>("Snapshotting:SnapshotIntervalSeconds") ?? 10);
            // Use the format defined for the photographer, as that's what it will produce.
            _snapshotFileExtension = configuration["Photographer:Format"]?.ToLowerInvariant() ?? "png";
            if (_snapshotFileExtension == "mjpeg") _snapshotFileExtension = "jpg"; // ffmpeg mjpeg format produces jpg files

            _logger.LogInformation("SnapshottingService configured: CheckActiveLivesInterval={CheckInterval}, SnapshotIntervalPerEvent={SnapshotInterval}, Format={Format}",
                _checkActiveLivesInterval, _snapshotIntervalPerEvent, _snapshotFileExtension);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("📸 SnapshottingHostedService is starting.");
            stoppingToken.Register(() => _logger.LogInformation("📸 SnapshottingHostedService is stopping (triggered by application shutdown)."));

            // Initial delay before first check, if desired
            // await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("📸 SnapshottingHostedService: Running main cycle to update active live event snapshot tasks.");
                try
                {
                    await ManageSnapshotTasksAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("📸 SnapshottingHostedService main loop canceled during ManageSnapshotTasksAsync.");
                    break; // Exit loop if service is stopping
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "📸 Unhandled error in SnapshottingHostedService main cycle. Will retry after delay.");
                }

                try
                {
                    await Task.Delay(_checkActiveLivesInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("📸 SnapshottingHostedService main loop delay canceled. Exiting.");
                    break; // Exit loop if service is stopping
                }
            }

            _logger.LogInformation("📸 SnapshottingHostedService has finished its main execution loop. Cleaning up...");
            await CleanupAllSnapshotTasksAsync();
            _logger.LogInformation("📸 SnapshottingHostedService has stopped.");
        }

        private async Task ManageSnapshotTasksAsync(CancellationToken stoppingToken)
        {
            List<LiveEvent> currentlyLiveEventsInDb;
            using (var scope = _scopeFactory.CreateScope())
            {
                var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
                var filter = Builders<LiveEvent>.Filter.Eq(l => l.IsLive, true);
                // Ensure we only get events with valid VideoId and Url
                filter &= Builders<LiveEvent>.Filter.Ne(l => l.VideoId, null) & Builders<LiveEvent>.Filter.Ne(l => l.VideoId, "");
                filter &= Builders<LiveEvent>.Filter.Ne(l => l.Url, null) & Builders<LiveEvent>.Filter.Ne(l => l.Url, "");
                
                currentlyLiveEventsInDb = await mongoService.FindAsync(filter);
            }

            _logger.LogDebug("📸 Found {Count} events marked as live in the database.", currentlyLiveEventsInDb.Count);
            var liveEventIdsFromDb = currentlyLiveEventsInDb.Select(l => l.VideoId!).ToHashSet(); // VideoId is now checked for non-null/empty

            lock (_lock)
            {
                // Stop snapshotting for events that are no longer live or missing from DB query
                var videoIdsCurrentlySnapshotting = _activeSnapshottingProcesses.Keys.ToList();
                foreach (var videoId in videoIdsCurrentlySnapshotting)
                {
                    if (!liveEventIdsFromDb.Contains(videoId))
                    {
                        _logger.LogInformation("📸 Event {VideoId} no longer marked as live or found in DB query. Requesting snapshot task to stop.", videoId);
                        if (_activeSnapshottingProcesses.TryGetValue(videoId, out var cts))
                        {
                            cts.Cancel(); // Signal the dedicated task to stop
                            // The task itself will handle removal from dictionary upon completion/cancellation
                        }
                    }
                }

                // Start/ensure snapshotting for events that are live
                foreach (var liveEvent in currentlyLiveEventsInDb)
                {
                    // VideoId and Url are already checked by the DB query filter
                    if (!_activeSnapshottingProcesses.ContainsKey(liveEvent.VideoId!))
                    {
                        _logger.LogInformation("📸 New live event {VideoId} detected for snapshotting. Starting dedicated task.", liveEvent.VideoId);
                        var newCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        _activeSnapshottingProcesses.Add(liveEvent.VideoId!, newCts);

                        // Start the snapshot loop for this event in a new task. Do not await.
                        _ = Task.Run(() => SnapshotLoopForSingleEventAsync(liveEvent, newCts), newCts.Token)
                            .ContinueWith(t => {
                                // This continuation runs when SnapshotLoopForSingleEventAsync completes or faults
                                lock(_lock) {
                                    if (_activeSnapshottingProcesses.TryGetValue(liveEvent.VideoId!, out var currentCts) && currentCts == newCts) {
                                        _activeSnapshottingProcesses.Remove(liveEvent.VideoId!);
                                        currentCts.Dispose();
                                        _logger.LogInformation("📸 Snapshot task for {VideoId} completed and removed from active list.", liveEvent.VideoId);
                                    }
                                }
                                if (t.IsFaulted) {
                                    _logger.LogError(t.Exception, "📸 Snapshot task for {VideoId} faulted.", liveEvent.VideoId);
                                } else if (t.IsCanceled) {
                                    _logger.LogInformation("📸 Snapshot task for {VideoId} was canceled.", liveEvent.VideoId);
                                }
                            }, TaskScheduler.Default);
                    }
                    else
                    {
                         _logger.LogTrace("📸 Snapshot task already active for {VideoId}.", liveEvent.VideoId);
                    }
                }
            }
        }

        private async Task SnapshotLoopForSingleEventAsync(LiveEvent liveEvent, CancellationTokenSource eventCtsSource)
        {
            var cancellationToken = eventCtsSource.Token;
            _logger.LogInformation("📸 Snapshot loop initiated for Event: {VideoId} ('{Title}'). Interval: {Interval}s.", liveEvent.VideoId, liveEvent.Title, _snapshotIntervalPerEvent.TotalSeconds);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Create a new scope for each snapshot attempt to resolve scoped services
                    // This ensures fresh instances and proper disposal, especially for DbContexts or HttpClients
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var photographerService = scope.ServiceProvider.GetRequiredService<IPhotographerService>();
                        var storageService = scope.ServiceProvider.GetRequiredService<IStorageService>();
                        var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>(); // For re-checking if still live

                        // Optional: Re-check if the event is still marked as live in DB before taking snapshot
                        var currentEventState = await mongoService.GetLiveEventByVideoIdAsync(liveEvent.VideoId!);
                        if (currentEventState == null || !currentEventState.IsLive)
                        {
                            _logger.LogInformation("📸 Event {VideoId} no longer live per DB check within its snapshot loop. Stopping.", liveEvent.VideoId);
                            if (!eventCtsSource.IsCancellationRequested) eventCtsSource.Cancel(); // Signal self to stop
                            break; 
                        }

                        _logger.LogDebug("📸 Taking snapshot for {VideoId} at {TimestampUtc}", liveEvent.VideoId, DateTime.UtcNow);
                        byte[]? snapshotData = await photographerService.TakeSnapshotAsync(liveEvent.Url!, liveEvent.VideoId!);

                        if (snapshotData != null && snapshotData.Length > 0)
                        {
                            string? filePath = await storageService.SaveSnapshotAsync(snapshotData, liveEvent.VideoId!, DateTime.UtcNow, _snapshotFileExtension);
                            if (!string.IsNullOrEmpty(filePath))
                            {
                                _logger.LogInformation("📸 Snapshot saved for {VideoId} to {FilePath}", liveEvent.VideoId, filePath);
                            }
                            else
                            {
                                _logger.LogWarning("📸 Failed to save snapshot for {VideoId} (storage service returned null/empty path).", liveEvent.VideoId);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("📸 Photographer returned null or empty data for {VideoId}. Skipping save.", liveEvent.VideoId);
                        }
                    } // Scope disposes here

                    await Task.Delay(_snapshotIntervalPerEvent, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("📸 Snapshot loop for {VideoId} was gracefully canceled.", liveEvent.VideoId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "📸 Unhandled error in snapshot loop for {VideoId}. Loop will terminate for this event.", liveEvent.VideoId);
                if (!eventCtsSource.IsCancellationRequested) eventCtsSource.Cancel(); // Ensure it stops on unhandled error
            }
            finally
            {
                _logger.LogInformation("📸 Snapshot loop for Event {VideoId} is terminating.", liveEvent.VideoId);
                // The ContinueWith on the Task.Run in ManageSnapshotTasksAsync handles cleanup from _activeSnapshottingProcesses
            }
        }

        private async Task CleanupAllSnapshotTasksAsync()
        {
            List<CancellationTokenSource> ctsToCancel;
            lock (_lock)
            {
                ctsToCancel = _activeSnapshottingProcesses.Values.ToList();
                _logger.LogInformation("📸 Requesting cancellation for {Count} active snapshot tasks during shutdown.", ctsToCancel.Count);
            }

            foreach (var cts in ctsToCancel)
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }

            // Give tasks a moment to attempt to finish gracefully.
            // You might want to Task.WhenAll on the actual Task objects if you store them,
            // but that can prolong shutdown. Signalling cancellation is usually sufficient.
            await Task.Delay(TimeSpan.FromSeconds(5)); // Adjust as needed

            lock(_lock)
            {
                foreach (var cts in _activeSnapshottingProcesses.Values.ToList()) // Re-iterate in case some finished and removed themselves
                {
                    cts.Dispose();
                }
                _activeSnapshottingProcesses.Clear();
            }
            _logger.LogInformation("📸 All active snapshotting processes have been signaled to stop and resources cleaned up.");
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("📸 SnapshottingHostedService.StopAsync called. Initiating cleanup.");
            await CleanupAllSnapshotTasksAsync(); // Ensure cleanup is called
            await base.StopAsync(stoppingToken); // Call base StopAsync
            _logger.LogInformation("📸 SnapshottingHostedService has fully stopped.");
        }
    }
}