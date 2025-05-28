using TCUWatcher.API.Models;
using TCUWatcher.API.Utils; // Para DateTimeUtils
using System; // Para DayOfWeek, TimeSpan, DateTime
using System.Globalization; // Para CultureInfo
using System.Linq; // Para Any
using System.Threading.Tasks; // Para Task
using Microsoft.Extensions.Logging; // Para ILogger
using Microsoft.Extensions.Configuration; // Para IConfiguration

namespace TCUWatcher.API.Services;

public class MonitoringScheduleService : IMonitoringScheduleService
{
    private readonly IMongoService _mongoService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MonitoringScheduleService> _logger;

    public MonitoringScheduleService(
        IMongoService mongoService,
        IConfiguration configuration,
        ILogger<MonitoringScheduleService> logger)
    {
        _mongoService = mongoService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> IsCurrentlyInMonitoringWindowAsync()
    {
        try
        {
            var activeWindows = await _mongoService.GetActiveMonitoringWindowsAsync().ConfigureAwait(false);
            if (!activeWindows.Any())
            {
                _logger.LogWarning("Nenhuma janela de monitoramento ativa configurada no MongoDB. O watcher NÃO será executado. (Ou fallback para appsettings.MonitoringHours)");
                // Opcional: Implementar fallback para as configurações em appsettings.json:MonitoringHours
                // return CheckFallbackMonitoringHours();
                return false; // Por agora, se não há config no DB, não roda.
            }

            DateTime nowBrasilia = DateTimeUtils.GetBrazilianDateTimeNow(_configuration);
            DayOfWeek currentDayOfWeek = nowBrasilia.DayOfWeek;
            TimeOnly currentTimeBrasilia = TimeOnly.FromDateTime(nowBrasilia); // .NET 6+

            // Para .NET Framework ou < .NET 6, use TimeSpan:
            // TimeSpan currentTimeBrasilia = nowBrasilia.TimeOfDay;


            foreach (var window in activeWindows)
            {
                if (window.DayOfWeek == currentDayOfWeek)
                {
                    if (TimeOnly.TryParseExact(window.StartTimeBrasilia, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly startTime) &&
                        TimeOnly.TryParseExact(window.EndTimeBrasilia, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly endTime))
                    {
                        // Para .NET Framework ou < .NET 6:
                        // if (TimeSpan.TryParseExact(window.StartTimeBrasilia, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan startTime) &&
                        //     TimeSpan.TryParseExact(window.EndTimeBrasilia, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan endTime))

                        if (currentTimeBrasilia >= startTime && currentTimeBrasilia < endTime)
                        {
                            _logger.LogInformation("Estamos DENTRO de uma janela de monitoramento ativa: {Description} ({DayOfWeek} das {Start} às {End}) - Horário Brasília.",
                                                   window.Description ?? "N/A", window.DayOfWeek, window.StartTimeBrasilia, window.EndTimeBrasilia);
                            return true;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Formato de hora inválido na configuração da janela de monitoramento ID '{WindowId}'. StartTime: '{StartTime}', EndTime: '{EndTime}'. Esperado 'HH:mm'.",
                                           window.Id, window.StartTimeBrasilia, window.EndTimeBrasilia);
                    }
                }
            }

            _logger.LogInformation("Estamos FORA de qualquer janela de monitoramento ativa configurada. Horário Brasília atual: {NowBrasilia} - {DayOfWeek} {TimeBrasilia}",
                                   nowBrasilia.ToString("dd/MM/yyyy HH:mm:ss"), currentDayOfWeek, currentTimeBrasilia.ToString("HH:mm"));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao verificar a janela de monitoramento. Assumindo que está FORA da janela para segurança.");
            return false; // Em caso de erro, não roda para evitar consumo desnecessário.
        }
    }

    // Método de fallback (opcional)
    private bool CheckFallbackMonitoringHours()
    {
        var startHourAppSetting = _configuration.GetValue<int?>("MonitoringHours:StartHour");
        var endHourAppSetting = _configuration.GetValue<int?>("MonitoringHours:EndHour");

        if (!startHourAppSetting.HasValue || !endHourAppSetting.HasValue)
        {
            _logger.LogWarning("Fallback para appsettings.MonitoringHours falhou: horas não configuradas.");
            return false; // Se não configurado, não roda
        }

        DateTime nowBrasilia = DateTimeUtils.GetBrazilianDateTimeNow(_configuration);
        if (nowBrasilia.Hour >= startHourAppSetting.Value && nowBrasilia.Hour < endHourAppSetting.Value)
        {
             _logger.LogInformation("Fallback: Estamos DENTRO da janela de monitoramento configurada em appsettings.json ({StartHour}h-{EndHour}h Brasília).", startHourAppSetting, endHourAppSetting);
            return true;
        }
        _logger.LogInformation("Fallback: Estamos FORA da janela de monitoramento configurada em appsettings.json ({StartHour}h-{EndHour}h Brasília).", startHourAppSetting, endHourAppSetting);
        return false;
    }
}
