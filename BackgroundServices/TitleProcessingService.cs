using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver; // For Builders&lt;LiveEvent&gt;
using System;
using System.Linq; // For .Any()
using System.Threading;
using System.Threading.Tasks;
using TCUWatcher.API.Models;    // For LiveEvent, ParsedTitleDetails, ProcessingStatus
using TCUWatcher.API.Services;   // For IMongoService, ITitleParserService

// Simplified Outline - requires IServiceScopeFactory, IMongoService, ITitleParserService, ILogger, IConfiguration
public class TitleProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TitleProcessingService> _logger;
    private readonly TimeSpan _checkInterval;

    public TitleProcessingService(IServiceScopeFactory scopeFactory, ILogger<TitleProcessingService> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _checkInterval = TimeSpan.FromMinutes(configuration.GetValue<int?>("TitleProcessing:CheckIntervalMinutes") ?? 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TitleProcessingService is starting.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
                var titleParser = scope.ServiceProvider.GetRequiredService<ITitleParserService>();

                // Find events where IsTitleProcessed is false
                var filter = Builders<LiveEvent>.Filter.Eq(e => e.IsTitleProcessed, false);
                var eventsToProcess = await mongoService.FindAsync(filter); // Assumes FindAsync can take just a filter

                _logger.LogInformation("Found {Count} events with unprocessed titles.", eventsToProcess.Count);

                foreach (var liveEvent in eventsToProcess)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    if (string.IsNullOrWhiteSpace(liveEvent.Title)) {
                         _logger.LogWarning("LiveEvent {Id} (VideoId: {VideoId}) has no title to process. Marking as processed.", liveEvent.Id, liveEvent.VideoId);
                         // Update LiveEvent: IsTitleProcessed = true, maybe add an error/note
                         var updateNoTitle = Builders<LiveEvent>.Update
                            .Set(e => e.IsTitleProcessed, true)
                            .Set(e => e.ParsedSessionType, "N/A - No Title"); // Or similar
                         await mongoService.UpdateLiveEventAsync(liveEvent.Id!, updateNoTitle);
                        continue;
                    }

                    _logger.LogInformation("Processing title for LiveEvent: {Id} (VideoId: {VideoId}): \"{Title}\"", liveEvent.Id, liveEvent.VideoId, liveEvent.Title);
                    ParsedTitleDetails? parsedDetails = await titleParser.ParseTitleAsync(liveEvent.Title);

                    if (parsedDetails != null)
                    {
                        var updateBuilder = Builders<LiveEvent>.Update
                            .Set(e => e.IsTitleProcessed, true)
                            .Set(e => e.ParsedColegiate, parsedDetails.Colegiate)
                            .Set(e => e.ParsedSessionType, parsedDetails.SessionType)
                            .Set(e => e.ParsedSessionDate, parsedDetails.SessionDate);
                            // Potentially log parsedDetails.ParsingErrors somewhere or to the LiveEvent document if needed

                        await mongoService.UpdateLiveEventAsync(liveEvent.Id!, updateBuilder);
                        _logger.LogInformation("Successfully processed and updated title for LiveEvent {Id}. Parsed: {Success}", liveEvent.Id, parsedDetails.WasSuccessfullyParsed);
                    }
                    else
                    {
                         _logger.LogWarning("Title parsing returned null for LiveEvent {Id}, Title: \"{Title}\". Marking as processed to avoid retries.", liveEvent.Id, liveEvent.Title);
                         var updateFailed = Builders<LiveEvent>.Update
                            .Set(e => e.IsTitleProcessed, true) // Mark processed to avoid retrying a problematic title indefinitely
                            .Set(e => e.ParsedSessionType, "N/A - Parsing Failed"); // Or similar
                         await mongoService.UpdateLiveEventAsync(liveEvent.Id!, updateFailed);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in TitleProcessingService loop.");
            }
            await Task.Delay(_checkInterval, stoppingToken);
        }
        _logger.LogInformation("TitleProcessingService is stopping.");
    }
}