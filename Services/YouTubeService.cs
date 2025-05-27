// COLE O CONTEÚDO COMPLETO DE YouTubeService.cs AQUI
using System.Text.Json;
using TCUWatcher.API.Models;
using TCUWatcher.API.Utils; // Para StringUtils
using Microsoft.Extensions.Configuration; // Para IConfiguration
using Microsoft.Extensions.Logging; // Para ILogger
using System; // Para Exception
using System.Collections.Generic; // Para List
using System.Linq; // Para Any
using System.Net.Http; // Para IHttpClientFactory
using System.Threading.Tasks; // Para Task
using MongoDB.Driver; // Para Builders (usado em CheckAndStoreLiveAsync indiretamente)

namespace TCUWatcher.API.Services;

public class YouTubeService : IYouTubeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMongoService _mongoService;
    private readonly INotifierService _notifierService;
    private readonly ILogger<YouTubeService> _logger;
    private readonly string? _apiKey;
    private readonly List<string> _titleKeywords;

    public YouTubeService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IMongoService mongoService,
        INotifierService notifierService,
        ILogger<YouTubeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _mongoService = mongoService;
        _notifierService = notifierService;
        _logger = logger;
        _apiKey = _configuration["YouTube:ApiKey"];
        _titleKeywords = _configuration.GetSection("YouTube:TitleKeywords")
                                      .Get<List<string>>() ?? new List<string>();
        if (!_titleKeywords.Any())
        {
            _logger.LogWarning("⚠️ Nenhuma palavra-chave de título configurada em 'YouTube:TitleKeywords'. Todas as lives serão consideradas relevantes.");
        }
    }

    private async Task<List<YouTubeSearchItem>> FetchLiveVideosFromApiAsync(string channelId)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("❌ YOUTUBE_API_KEY ausente. Não é possível buscar lives para o canal {ChannelId}.", channelId);
            return new List<YouTubeSearchItem>();
        }

        var url = $"https://www.googleapis.com/youtube/v3/search?part=snippet&channelId={channelId}&eventType=live&type=video&key={_apiKey}";
        _logger.LogDebug("🌐 Chamando API do YouTube para o canal {ChannelId}: {Url}", channelId, url);

        try
        {
            var client = _httpClientFactory.CreateClient("YouTube");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogError("🛑 Erro na chamada à API do YouTube para {ChannelId} ({StatusCode}): {ErrorContent}", channelId, response.StatusCode, errorContent);
                return new List<YouTubeSearchItem>();
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var data = await JsonSerializer.DeserializeAsync<YouTubeSearchResponse>(contentStream).ConfigureAwait(false);

            if (data?.Items == null || !data.Items.Any())
            {
                _logger.LogInformation("📭 Nenhuma live encontrada pela API para o canal {ChannelId}.", channelId);
                return new List<YouTubeSearchItem>();
            }
            return data.Items;
        }
        catch (HttpRequestException e)
        {
            _logger.LogError(e, "🛑 Erro HTTP na chamada à API do YouTube para o canal {ChannelId}.", channelId);
            return new List<YouTubeSearchItem>();
        }
        catch (JsonException e)
        {
            _logger.LogError(e, "🛑 Erro de JSON ao processar resposta da API do YouTube para o canal {ChannelId}.", channelId);
            return new List<YouTubeSearchItem>();
        }
        catch (TaskCanceledException e)
        {
            _logger.LogWarning(e, "Timeout ao buscar lives da API do YouTube para o canal {ChannelId}.", channelId);
            return new List<YouTubeSearchItem>();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "🛑 Erro inesperado ao buscar lives da API para o canal {ChannelId}.", channelId);
            return new List<YouTubeSearchItem>();
        }
    }

    public async Task<List<string>> CheckAndStoreLiveAsync(string channelId)
    {
        _logger.LogInformation("🔁 Iniciando checagem e armazenamento de lives para o canal {ChannelId}", channelId);
        var activeVideoIds = new List<string>();

        var items = await FetchLiveVideosFromApiAsync(channelId).ConfigureAwait(false);
        if (!items.Any())
        {
            _logger.LogInformation("Nenhuma live para processar no canal {ChannelId}.", channelId);
            return activeVideoIds;
        }

        foreach (var item in items)
        {
            if (!IsTitleRelevant(item.Snippet.Title))
            {
                _logger.LogInformation("ℹ️ Live '{VideoId}' ('{Title}') ignorada para o canal {ChannelId} devido ao título não relevante.", item.Id.VideoId, item.Snippet.Title, channelId);
                continue;
            }
            _logger.LogInformation("👍 Live '{VideoId}' ('{Title}') com título relevante para o canal {ChannelId}.", item.Id.VideoId, item.Snippet.Title, channelId);

            var videoId = item.Id.VideoId;
            var title = item.Snippet.Title;
            activeVideoIds.Add(videoId);

            try
            {
                var existingDoc = await _mongoService.GetLiveEventByVideoIdAsync(videoId).ConfigureAwait(false);

                if (existingDoc == null)
                {
                    var newLiveEvent = new LiveEvent
                    {
                        Title = title,
                        VideoId = videoId,
                        StartedAt = DateTime.UtcNow,
                        Url = $"https://www.youtube.com/watch?v={videoId}", // URL correta do YouTube
                        ChannelId = item.Snippet.ChannelId,
                        IsLive = true,
                        MissCount = 0
                    };
                    await _mongoService.CreateLiveEventAsync(newLiveEvent).ConfigureAwait(false);
                    _logger.LogInformation("🆕 Nova live salva: {VideoId} ({Title})", videoId, title);
                    await _notifierService.SendWebhookAsync(newLiveEvent).ConfigureAwait(false);
                }
                else
                {
                    if (existingDoc.Id != null && (!existingDoc.IsLive || existingDoc.MissCount > 0 || existingDoc.Title != title || existingDoc.ChannelId != item.Snippet.ChannelId))
                    {
                        var updateBuilder = Builders<LiveEvent>.Update;
                        var updates = new List<UpdateDefinition<LiveEvent>>
                        {
                            updateBuilder.Set(l => l.IsLive, true),
                            updateBuilder.Set(l => l.MissCount, 0)
                        };
                        if (existingDoc.Title != title) updates.Add(updateBuilder.Set(l => l.Title, title));
                        if (existingDoc.ChannelId != item.Snippet.ChannelId) updates.Add(updateBuilder.Set(l => l.ChannelId, item.Snippet.ChannelId));
                        // Atualiza a URL se ela tiver mudado (caso o padrão mude)
                        string newUrl = $"https://www.youtube.com/watch?v={videoId}";
                        if(existingDoc.Url != newUrl) updates.Add(updateBuilder.Set(l => l.Url, newUrl));


                        await _mongoService.UpdateLiveEventAsync(existingDoc.Id, updateBuilder.Combine(updates)).ConfigureAwait(false);
                        _logger.LogInformation("🔄 Live reativada/atualizada: {VideoId}", videoId);
                    }
                    else
                    {
                        _logger.LogDebug("Live '{VideoId}' já está ativa e não precisa de atualização.", videoId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar live '{VideoId}' para o canal {ChannelId}.", videoId, channelId);
            }
        }
        _logger.LogInformation("🏁 Checagem e armazenamento de lives concluídos para o canal {ChannelId}.", channelId);
        return activeVideoIds;
    }

    private bool IsTitleRelevant(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }
        if (!_titleKeywords.Any())
        {
            return true;
        }

        foreach (var keyword in _titleKeywords)
        {
            if (title.ContainsIgnoreCaseAndAccents(keyword))
            {
                return true;
            }
        }
        return false;
    }
}
