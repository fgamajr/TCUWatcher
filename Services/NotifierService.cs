// COLE O CONTEÚDO COMPLETO DE NotifierService.cs AQUI
using System.Net.Http; // Para IHttpClientFactory, HttpContent
using System.Net.Http.Json; // Para PostAsJsonAsync
using System.Text.Json;
using TCUWatcher.API.Models;
using Microsoft.Extensions.Configuration; // Para IConfiguration
using Microsoft.Extensions.Logging; // Para ILogger
using System; // Para Exception
using System.Collections.Generic; // Para Dictionary
using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.Services;

public class NotifierService : INotifierService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotifierService> _logger;
    private readonly string? _webhookUrl;

    public NotifierService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NotifierService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _webhookUrl = _configuration["Webhook:Url"];
    }

    private async Task SendWebhookInternalAsync(HttpContent content)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            _logger.LogInformation("🔕 Nenhum webhook configurado. Notificação não enviada.");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("Notifier");
            using var response = await client.PostAsync(_webhookUrl, content).ConfigureAwait(false);
            
            if (response.IsSuccessStatusCode)
            {
                 _logger.LogInformation("📬 Webhook enviado com sucesso para {WebhookUrl}", _webhookUrl);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _logger.LogWarning("Falha ao enviar webhook para {WebhookUrl}. Status: {StatusCode}. Resposta: {ResponseBody}",
                                   _webhookUrl, response.StatusCode, responseBody);
            }
        }
        catch (HttpRequestException e)
        {
            _logger.LogError(e, "Erro de rede/HTTP ao enviar webhook para {WebhookUrl}.", _webhookUrl);
        }
        catch (TaskCanceledException e)
        {
            _logger.LogWarning(e, "Timeout ao enviar webhook para {WebhookUrl}. A requisição demorou muito para responder.", _webhookUrl);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Erro inesperado ao enviar webhook para {WebhookUrl}.", _webhookUrl);
        }
    }

    public async Task SendWebhookAsync(LiveEvent eventData)
    {
        var payload = new 
        { 
            eventData.Title, 
            eventData.VideoId, 
            eventData.Url, 
            Status = eventData.IsLive ? "Online" : "Offline",
            StartedAt = eventData.StartedAt.ToString("o")
        };
        
        var content = JsonContent.Create(payload);
        await SendWebhookInternalAsync(content).ConfigureAwait(false);
    }
    
    public async Task SendWebhookAsync(Dictionary<string, string> eventData)
    {
        var content = JsonContent.Create(eventData);
        await SendWebhookInternalAsync(content).ConfigureAwait(false);
    }
}
