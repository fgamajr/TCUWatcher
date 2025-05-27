// COLE O CONTEÚDO COMPLETO DE CheckController.cs AQUI
using Microsoft.AspNetCore.Mvc;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;
using System.Text;
using MongoDB.Driver;
using Microsoft.AspNetCore.Http; // Para StatusCodes
using Microsoft.Extensions.Logging; // Para ILogger
using System; // Para Exception, StringComparison
using System.Threading.Tasks; // Para Task
using System.Collections.Generic; // Para Dictionary

namespace TCUWatcher.API.Controllers;

[ApiController]
[Route("[controller]")]
public class CheckController : ControllerBase
{
    private readonly IMongoService _mongoService;
    private readonly INotifierService _notifierService;
    private readonly ILogger<CheckController> _logger;

    public CheckController(
        IMongoService mongoService,
        INotifierService notifierService,
        ILogger<CheckController> logger)
    {
        _mongoService = mongoService;
        _notifierService = notifierService;
        _logger = logger;
    }

    [HttpGet("lives")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ListLives([FromQuery] string? status, [FromQuery] string? canal)
    {
        _logger.LogInformation("Endpoint ListLives acessado com status: '{Status}', canal: '{Canal}'.", status, canal);
        try
        {
            var builder = Builders<LiveEvent>.Filter;
            var filter = builder.Empty;

            if (!string.IsNullOrEmpty(status))
            {
                if (status.Equals("online", StringComparison.OrdinalIgnoreCase))
                {
                    filter &= builder.Eq(l => l.IsLive, true);
                }
                else if (status.Equals("offline", StringComparison.OrdinalIgnoreCase))
                {
                    filter &= builder.Eq(l => l.IsLive, false);
                }
            }
            if (!string.IsNullOrEmpty(canal))
            {
                filter &= builder.Or(
                    builder.Regex(l => l.ChannelId, new MongoDB.Bson.BsonRegularExpression(canal, "i")),
                    builder.Regex(l => l.Title, new MongoDB.Bson.BsonRegularExpression(canal, "i")),
                    builder.Regex(l => l.VideoId, new MongoDB.Bson.BsonRegularExpression(canal, "i"))
                );
            }

            var sort = Builders<LiveEvent>.Sort.Descending(l => l.StartedAt);
            var docs = await _mongoService.GetLiveEventsAsync(filter, sort).ConfigureAwait(false);

            _logger.LogInformation("Retornando {Count} lives.", docs.Count);
            return Ok(new { total = docs.Count, lives = docs });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado ao listar lives com status '{Status}' e canal '{Canal}'.", status, canal);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Ocorreu um erro interno ao processar sua solicitação." });
        }
    }
    
    [HttpGet("mock-live")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SimulateLive()
    {
        _logger.LogInformation("Endpoint SimulateLive acessado.");
        try
        {
            var fakeVideoId = "simul_" + GenerateRandomString(6);
            var eventData = new LiveEvent
            {
                Title = "Simulação de Live do TCU - Plenário",
                VideoId = fakeVideoId,
                Url = $"https://www.youtube.com/watch?v={fakeVideoId}",
                StartedAt = DateTime.UtcNow,
                IsLive = true,
                ChannelId = "mock_channel_tcu",
                MissCount = 0
            };

            await _mongoService.CreateLiveEventAsync(eventData).ConfigureAwait(false);
            _logger.LogInformation("✅ Live simulada registrada: {VideoId}", eventData.VideoId);
            
            await _notifierService.SendWebhookAsync(eventData).ConfigureAwait(false);
            
            return Ok(new { simulated = true, live_event = eventData });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado ao simular live.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Ocorreu um erro interno ao processar sua solicitação de simulação." });
        }
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        var stringBuilder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            stringBuilder.Append(chars[random.Next(chars.Length)]);
        }
        return stringBuilder.ToString();
    }
}
