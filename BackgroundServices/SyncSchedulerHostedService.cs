using TCUWatcher.API.Services;
using Microsoft.Extensions.DependencyInjection; // Para IServiceProvider e CreateAsyncScope
using Microsoft.Extensions.Hosting; // Para BackgroundService
using Microsoft.Extensions.Logging; // Para ILogger
using Microsoft.Extensions.Configuration; // Para IConfiguration
using System; // Para Exception, TimeSpan
using System.Threading; // Para CancellationToken
using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.BackgroundServices;

public class SyncSchedulerHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyncSchedulerHostedService> _logger;
    private readonly int _syncIntervalMinutes;
    private readonly int _initialDelaySeconds;

    public SyncSchedulerHostedService(
        IServiceProvider serviceProvider,
        ILogger<SyncSchedulerHostedService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _syncIntervalMinutes = configuration.GetValue<int>("Scheduler:SyncIntervalMinutes", 30);
        _initialDelaySeconds = configuration.GetValue<int>("Scheduler:InitialDelaySeconds", 15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("⏰ SyncSchedulerHostedService está iniciando. Primeira execução agendada para daqui a {InitialDelaySeconds} segundos.", _initialDelaySeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_initialDelaySeconds), stoppingToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) // CS1524 - Correção: Adicionado catch
        {
            _logger.LogInformation("⏰ SyncSchedulerHostedService cancelado durante o atraso inicial. Serviço não iniciará a sincronização.");
            return; // Sai se o serviço for cancelado antes da primeira execução
        }
        catch (Exception ex) // CS1524 - Correção: Adicionado catch genérico
        {
            _logger.LogError(ex, "Erro inesperado durante o atraso inicial do SyncSchedulerHostedService. Serviço não iniciará a sincronização.");
            return;
        }


        while (!stoppingToken.IsCancellationRequested)
        {
            bool isInMonitoringWindow = false;
            try
            {
                await using (var scope = _serviceProvider.CreateAsyncScope())
                {
                    var monitoringScheduleService = scope.ServiceProvider.GetRequiredService<IMonitoringScheduleService>();
                    isInMonitoringWindow = await monitoringScheduleService.IsCurrentlyInMonitoringWindowAsync().ConfigureAwait(false);
                }

                if (isInMonitoringWindow)
                {
                    _logger.LogInformation("⏰ SyncSchedulerHostedService está executando a tarefa de sincronização agendada (DENTRO da janela).");
                    await using (var scope = _serviceProvider.CreateAsyncScope())
                    {
                        var syncService = scope.ServiceProvider.GetRequiredService<ISyncService>();
                        await syncService.FullSyncLivesAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogInformation("💤 SyncSchedulerHostedService pulando a sincronização. Fora da janela de monitoramento configurada.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ocorreu um erro durante a execução do ciclo do SyncSchedulerHostedService.");
            }

            _logger.LogInformation("⏰ Próxima verificação de janela/sincronização em {Interval} minutos.", _syncIntervalMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_syncIntervalMinutes), stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) // CS1524 - Correção: Adicionado catch
            {
                _logger.LogInformation("⏰ SyncSchedulerHostedService cancelado enquanto aguardava o próximo intervalo. O serviço está parando.");
                break; // Sai do loop se o serviço for cancelado
            }
            catch (Exception ex) // CS1524 - Correção: Adicionado catch genérico
            {
                _logger.LogError(ex, "Erro inesperado durante o atraso do SyncSchedulerHostedService. O serviço pode continuar, mas o agendamento pode ser afetado.");
            }
        }

        _logger.LogInformation("⏰ SyncSchedulerHostedService está parando.");
    }
}