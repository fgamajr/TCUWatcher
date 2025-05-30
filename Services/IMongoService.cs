using TCUWatcher.API.Models; // Certifique-se que esta linha está presente
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

    /// <summary>
    /// Busca a configuração global da aplicação no MongoDB.
    /// </summary>
    /// <returns>O objeto AppConfiguration se encontrado, caso contrário, null.</returns>
    Task<AppConfiguration?> GetAppConfigurationAsync(); // <<< ESTE ESTAVA FALTANDO!

    /// <summary>
    /// Salva ou atualiza a configuração global da aplicação no MongoDB.
    /// </summary>
    /// <param name="config">O objeto AppConfiguration a ser salvo.</param>
    Task SaveAppConfigurationAsync(AppConfiguration config);

    Task<LiveEvent?> GetLiveEventByFileHashAsync(string fileHash);

    Task EnsureIndexesAsync(); // New method
}