using TCUWatcher.API.Models;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration; // Para IConfiguration (ainda pode ser útil para coisas não-dinâmicas)
using Microsoft.Extensions.Logging;      // Para ILogger
using Microsoft.Extensions.DependencyInjection; // Para IServiceProvider e CreateAsyncScope
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services;

/// <summary>
/// Serviço responsável pela sincronização completa de lives.
/// </summary>
public class SyncService : ISyncService
{
    private readonly IMongoService _primaryMongoService; // MongoService injetado diretamente para algumas operações
    private readonly IYouTubeService _youTubeService;
    private readonly IConfiguration _configuration; // Pode ser usado para configs que não mudam via DB
    private readonly ILogger<SyncService> _logger;
    private readonly IServiceProvider _serviceProvider; // Para criar escopos e obter serviços como IMongoService

    /// <summary>
    /// Inicializa uma nova instância do serviço SyncService.
    /// </summary>
    /// <param name="mongoService">O serviço de MongoDB (para operações que não precisam de escopo novo).</param>
    /// <param name="youTubeService">O serviço do YouTube.</param>
    /// <param name="configuration">A configuração da aplicação.</param>
    /// <param name="logger">O logger para registrar informações.</param>
    /// <param name="serviceProvider">O provedor de serviços para criar escopos para obter o IMongoService.</param>
    public SyncService(
        IMongoService mongoService, // Injetado para acesso direto se necessário
        IYouTubeService youTubeService,
        IConfiguration configuration,
        ILogger<SyncService> logger,
        IServiceProvider serviceProvider)
    {
        _primaryMongoService = mongoService; // Usar o injetado diretamente quando não precisar de escopo novo
        _youTubeService = youTubeService;
        _configuration = configuration;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public async Task FullSyncLivesAsync()
    {
        _logger.LogInformation("🚀 Iniciando FullSyncLives...");

        // Carregar configurações dinâmicas do MongoDB
        int maxMissCount = 2; // Valor padrão de fallback
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
            var appConfig = await mongoService.GetAppConfigurationAsync().ConfigureAwait(false);
            if (appConfig != null)
            {
                maxMissCount = appConfig.MaxMissCountBeforeOffline;
                _logger.LogInformation("Configuração MaxMissCountBeforeOffline carregada do DB: {MaxMissCount}", maxMissCount);
            }
            else
            {
                _logger.LogWarning("Configuração da aplicação (AppConfiguration) não encontrada no MongoDB. Usando MaxMissCountBeforeOffline padrão: {DefaultMaxMissCount}.", maxMissCount);
            }
        }
        
        var channelsConfig = _configuration["YouTube:Channels"]; // Canais ainda podem vir do appsettings/user-secrets/env vars
        if (string.IsNullOrEmpty(channelsConfig))
        {
            _logger.LogWarning("⚠️ CHANNELS não configurado. Sincronização abortada.");
            return;
        }
        var channels = channelsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!channels.Any())
        {
            _logger.LogWarning("⚠️ Lista de CHANNELS está vazia. Sincronização abortada.");
            return;
        }

        // 1) Busca as lives que hoje estão marcadas ativas no banco de dados.
        var filterActive = Builders<LiveEvent>.Filter.Eq(l => l.IsLive, true);
        List<LiveEvent> currentlyActiveLives;
        try
        {
            // Usar _primaryMongoService aqui, pois é uma leitura que não deve interferir com outros escopos
            currentlyActiveLives = await _primaryMongoService.FindAsync(filterActive).ConfigureAwait(false);
            _logger.LogInformation("📊 {Count} lives atualmente marcadas como ativas no banco de dados.", currentlyActiveLives.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar lives ativas do MongoDB. Abortando sincronização.");
            return;
        }
        
        // 2) Dispara checagem dos canais via API do YouTube e coleta IDs ativos detectados.
        var detectedActiveVideoIds = new List<string>();
        foreach (var channelId in channels)
        {
            if (string.IsNullOrWhiteSpace(channelId)) continue;
            _logger.LogInformation("📡 Processando canal: {ChannelId}", channelId);
            try
            {
                // _youTubeService é singleton, suas dependências (como IMongoService) também são singleton ou
                // são resolvidas internamente de forma segura.
                var idsFromChannel = await _youTubeService.CheckAndStoreLiveAsync(channelId).ConfigureAwait(false);
                detectedActiveVideoIds.AddRange(idsFromChannel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao sincronizar o canal {ChannelId}. Continuando com o próximo canal.", channelId);
            }
        }
        _logger.LogInformation("📡 Total de {Count} lives detectadas como ativas pela API do YouTube (após filtro de título).", detectedActiveVideoIds.Count);


        // 3) Processa encerramentos graduais para lives que estavam ativas mas não foram mais detectadas.
        foreach (var live in currentlyActiveLives)
        {
            if (live.Id == null)
            {
                _logger.LogWarning("Live encontrada sem ID no banco de dados: {VideoId}. Pulando.", live.VideoId);
                continue;
            }

            if (!detectedActiveVideoIds.Contains(live.VideoId))
            {
                int newMissCount = live.MissCount + 1;
                UpdateDefinition<LiveEvent> update;

                if (newMissCount >= maxMissCount)
                {
                    update = Builders<LiveEvent>.Update
                        .Set(l => l.IsLive, false)
                        .Set(l => l.MissCount, newMissCount)
                        .Set(l => l.EndedAt, DateTime.UtcNow);
                    _logger.LogInformation("⛔ Live encerrada (missCount={MissCount}/{MaxMissCount}): {VideoId} ({Title})", newMissCount, maxMissCount, live.VideoId, live.Title);
                }
                else
                {
                    update = Builders<LiveEvent>.Update
                        .Set(l => l.MissCount, newMissCount);
                    _logger.LogInformation("⚠️ Live ausente (missCount={MissCount}/{MaxMissCount}): {VideoId} ({Title})", newMissCount, maxMissCount, live.VideoId, live.Title);
                }

                try
                {
                    // Usar _primaryMongoService aqui também
                    await _primaryMongoService.UpdateLiveEventAsync(live.Id, update).ConfigureAwait(false);
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