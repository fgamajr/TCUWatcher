using TCUWatcher.API.Models;
using MongoDB.Driver;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services;

public interface IMongoService
{
    Task<List<LiveEvent>> GetLiveEventsAsync(FilterDefinition<LiveEvent> filter, SortDefinition<LiveEvent> sort);
    Task<LiveEvent?> GetLiveEventByVideoIdAsync(string videoId);
    Task CreateLiveEventAsync(LiveEvent liveEvent);
    Task<bool> UpdateLiveEventAsync(string id, UpdateDefinition<LiveEvent> update);
    Task<List<LiveEvent>> FindAsync(FilterDefinition<LiveEvent> filter);
    Task<List<MonitoringWindow>> GetActiveMonitoringWindowsAsync();
}