// COLE O CONTEÚDO COMPLETO DE DashboardController.cs AQUI
using Microsoft.AspNetCore.Mvc;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;
using TCUWatcher.API.Utils;
using MongoDB.Driver;
using Microsoft.Extensions.Logging; // Para ILogger
using Microsoft.Extensions.Configuration; // Para IConfiguration
using System; // Para Exception, StringComparison
using System.Threading.Tasks; // Para Task
using System.Collections.Generic; // Para List
using System.Linq; // Para Any

namespace TCUWatcher.API.Controllers;

public class DashboardController : Controller
{
    private readonly IMongoService _mongoService;
    private readonly ILogger<DashboardController> _logger;
    private readonly IConfiguration _configuration; // Adicionado para DateTimeUtils

    public DashboardController(IMongoService mongoService, ILogger<DashboardController> logger, IConfiguration configuration)
    {
        _mongoService = mongoService;
        _logger = logger;
        _configuration = configuration; // Injetar IConfiguration
    }

    [HttpGet("/dashboard")]
    public async Task<IActionResult> Index([FromQuery] string? canal, [FromQuery] string? status)
    {
        _logger.LogInformation("Endpoint Dashboard/Index acessado com canal: '{Canal}', status: '{Status}'.", canal, status);
        try
        {
            var builder = Builders<LiveEvent>.Filter;
            var filter = builder.Empty;

            if (!string.IsNullOrEmpty(canal))
            {
                filter &= builder.Or(
                    builder.Regex(l => l.ChannelId, new MongoDB.Bson.BsonRegularExpression(canal, "i")),
                    builder.Regex(l => l.Title, new MongoDB.Bson.BsonRegularExpression(canal, "i")),
                    builder.Regex(l => l.VideoId, new MongoDB.Bson.BsonRegularExpression(canal, "i"))
                );
            }

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

            var sort = Builders<LiveEvent>.Sort.Descending(l => l.StartedAt);
            var lives = await _mongoService.GetLiveEventsAsync(filter, sort).ConfigureAwait(false);

            foreach (var live in lives)
            {
                // Passar IConfiguration para FormatDateTime
                live.FormattedDate = DateTimeUtils.FormatDateTime(live.StartedAt, _configuration);
            }
            
            ViewData["CanalFilter"] = canal;
            ViewData["StatusFilter"] = status;

            _logger.LogInformation("Dashboard carregado com {Count} lives.", lives.Count);
            return View(lives);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado ao carregar o dashboard com canal '{Canal}' e status '{Status}'.", canal, status);
            ViewData["ErrorMessage"] = "Ocorreu um erro ao carregar os dados do dashboard. Por favor, tente novamente.";
            return View(new List<LiveEvent>());
        }
    }
}
