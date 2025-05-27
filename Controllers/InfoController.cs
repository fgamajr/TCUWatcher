// COLE O CONTEÚDO COMPLETO DE InfoController.cs AQUI
using Microsoft.AspNetCore.Mvc;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;
using TCUWatcher.API.Utils;
using MongoDB.Driver;
using Microsoft.Extensions.Logging; // Para ILogger
using Microsoft.Extensions.Configuration; // Para IConfiguration
using System; // Para Exception
using System.Threading.Tasks; // Para Task
using System.Linq; // Para FirstOrDefault

namespace TCUWatcher.API.Controllers;

[ApiController]
[Route("[controller]")]
public class InfoController : ControllerBase
{
    private readonly IMongoService _mongoService;
    private readonly ILogger<InfoController> _logger;
    private readonly IConfiguration _configuration;

    public InfoController(IMongoService mongoService, ILogger<InfoController> logger, IConfiguration configuration)
    {
        _mongoService = mongoService;
        _logger = logger;
        _configuration = configuration;
    }

    [HttpGet("/")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRoot()
    {
        _logger.LogInformation("Endpoint '/' acessado.");
        return Ok(new { message = "TCU Watcher API online" });
    }

    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStatus()
    {
        _logger.LogInformation("Endpoint '/info/status' acessado.");
        try
        {
            var nowBrazil = DateTimeUtils.GetBrazilianDateTimeNow(_configuration);

            var filter = Builders<LiveEvent>.Filter.Empty;
            var sort = Builders<LiveEvent>.Sort.Descending(l => l.StartedAt);
            
            var liveEvents = await _mongoService.GetLiveEventsAsync(filter, sort).ConfigureAwait(false);
            var lastEvent = liveEvents.FirstOrDefault();

            _logger.LogInformation("Status consultado. Última live detectada: {VideoId}", lastEvent?.VideoId ?? "Nenhuma");
            return Ok(new
            {
                status = "ok",
                agora_brasilia = nowBrazil.ToString("o"),
                ultima_live_detectada = lastEvent 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado ao obter status da aplicação.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Ocorreu um erro interno ao processar sua solicitação de status." });
        }
    }
}
