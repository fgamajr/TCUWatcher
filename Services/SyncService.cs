// COLE O CONTEÚDO COMPLETO DE SyncService.cs AQUI
using TCUWatcher.API.Models;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration; // Para IConfiguration
using Microsoft.Extensions.Logging; // Para ILogger
using System; // Para Exception
using System.Collections.Generic; // Para List
using System.Linq; // Para Any
using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.Services;

public class SyncService : ISyncService
{
    private readonly IMongoService _mongoService;
    private readonly IYouTubeService _youTubeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SyncService> _logger;
    private readonly int _maxMissCountBeforeOffline;

    public SyncService(
        IMongoService mongoService,
        IYouTubeService youTubeService,
        IConfiguration configuration,
        ILogger<SyncService> logger)
    {
        _mongoService = mongoService;
        _youTubeService = youTubeService;
        _configuration = configuration;
        _logger = logger;
        _maxMissCountBeforeOffline = _configuration.GetValue<int>("Scheduler:MaxMissCountBeforeOffline", 2);
    }

    public async Task FullSyncLivesAsync()
    {
        _logger.LogInformation("🚀 Iniciando FullSyncLives...");
        
        var channelsConfig = _configuration["YouTube:Channels"];
        if (string.IsNullOrEmpty(channelsConfig))
        {
            _logger.LogWarning("⚠️ CHANNELS não configurado em appsettings.json. Sincronização abortada.");
            return;
        }
        var channels = channelsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!channels.Any())
        {
            _logger.LogWarning("⚠️ Lista de CHANNELS está vazia. Sincronização abortada.");
            return;
        }

        var filterActive = Builders<LiveEvent>.Filter.Eq(l => l.IsLive, true);
        List<LiveEvent> currentlyActiveLives;
        try
        {
            currentlyActiveLives = await _mongoService.FindAsync(filterActive).ConfigureAwait(false);
            _logger.LogInformation("📊 {Count} lives atualmente marcadas como ativas no banco de dados.", currentlyActiveLives.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar lives ativas do MongoDB. Abortando sincronização.");
            return;
        }
        
        var detectedActiveVideoIds = new List<string>();
        foreach (var channelId in channels)
        {
            if (string.IsNullOrWhiteSpace(channelId)) continue;
            _logger.LogInformation("📡 Processando canal: {ChannelId}", channelId);
            try
            {
                var idsFromChannel = await _youTubeService.CheckAndStoreLiveAsync(channelId).ConfigureAwait(false);
                detectedActiveVideoIds.AddRange(idsFromChannel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao sincronizar o canal {ChannelId}. Continuando com o próximo canal.", channelId);
            }
        }
        _logger.LogInformation("📡 Total de {Count} lives detectadas como ativas pela API do YouTube (após filtro de título).", detectedActiveVideoIds.Count);

        foreach (var live in currentlyActiveLives)
        {
            if (live.Id == null) continue;

            if (!detectedActiveVideoIds.Contains(live.VideoId))
            {
                int newMissCount = live.MissCount + 1;
                UpdateDefinition<LiveEvent> update;

                if (newMissCount >= _maxMissCountBeforeOffline)
                {
                    update = Builders<LiveEvent>.Update
                        .Set(l => l.IsLive, false)
                        .Set(l => l.MissCount, newMissCount)
                        .Set(l => l.EndedAt, DateTime.UtcNow);
                    _logger.LogInformation("⛔ Live encerrada (missCount={MissCount}): {VideoId} ({Title})", newMissCount, live.VideoId, live.Title);
                }
                else
                {
                    update = Builders<LiveEvent>.Update
                        .Set(l => l.MissCount, newMissCount);
                    _logger.LogInformation("⚠️ Live ausente (missCount={MissCount}): {VideoId} ({Title})", newMissCount, live.VideoId, live.Title);
                }

                try
                {
                    await _mongoService.UpdateLiveEventAsync(live.Id, update).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao atualizar o status da live '{VideoId}' para missCount={MissCount}.", live.VideoId, newMissCount);
                }
            }
        }
        _logger.LogInformation("🏁 FullSyncLives concluído.");
    }
}
