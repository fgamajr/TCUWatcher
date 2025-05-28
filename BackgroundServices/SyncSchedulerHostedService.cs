using TCUWatcher.API.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Ainda necessário para TimeZone no MonitoringScheduleService
using System;
using System.Threading;
using System.Threading.Tasks;
using TCUWatcher.API.Models;

namespace TCUWatcher.API.BackgroundServices;

/// <summary>
/// Serviço de background hospedado para agendar e executar a sincronização completa de lives.
/// </summary>
public class SyncSchedulerHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyncSchedulerHostedService> _logger;
    // Removidas as variáveis de intervalo e delay do construtor, serão lidas do DB

    public SyncSchedulerHostedService(
        IServiceProvider serviceProvider,
        ILogger<SyncSchedulerHostedService> logger) // IConfiguration não é mais injetado aqui diretamente
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Executa a lógica principal do serviço de background.
    /// </summary>
    /// <param name="stoppingToken">Um token de cancelamento que indica quando o serviço deve parar.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("⏰ SyncSchedulerHostedService está iniciando.");

        int currentSyncIntervalMinutes = 30; // Valor padrão de fallback
        int currentInitialDelaySeconds = 15; // Valor padrão de fallback

        // Tenta buscar a configuração inicial do DB
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
            var appConfig = await mongoService.GetAppConfigurationAsync().ConfigureAwait(false);

            if (appConfig == null)
            {
                _logger.LogWarning("Nenhuma configuração de aplicação encontrada no MongoDB. Criando uma configuração padrão e usando valores padrão.");
                appConfig = new AppConfiguration(); // Cria uma instância com os valores padrão
                await mongoService.SaveAppConfigurationAsync(appConfig).ConfigureAwait(false); // Salva no DB
            }
            
            currentSyncIntervalMinutes = appConfig.SyncIntervalMinutes;
            currentInitialDelaySeconds = appConfig.InitialDelaySeconds;
        }

        _logger.LogInformation("⏰ Primeira execução agendada para daqui a {InitialDelaySeconds} segundos, com intervalo de {SyncIntervalMinutes} minutos.", currentInitialDelaySeconds, currentSyncIntervalMinutes);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(currentInitialDelaySeconds), stoppingToken).ConfigureAwait(false);
        }
        catch(TaskCanceledException)
        {
            _logger.LogInformation("⏰ SyncSchedulerHostedService cancelado durante o atraso inicial. Serviço não iniciará a sincronização.");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro inesperado durante o atraso inicial do SyncSchedulerHostedService. Serviço não iniciará a sincronização.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-busca a configuração em cada iteração para pegar alterações em tempo real
            await using (var scope = _serviceProvider.CreateAsyncScope())
            {
                var mongoService = scope.ServiceProvider.GetRequiredService<IMongoService>();
                var appConfig = await mongoService.GetAppConfigurationAsync().ConfigureAwait(false);
                if (appConfig != null)
                {
                    currentSyncIntervalMinutes = appConfig.SyncIntervalMinutes;
                    // initialDelaySeconds não é relevante aqui, já foi usado no início.
                    // maxMissCountBeforeOffline será lido pelo SyncService.
                }
                else
                {
                    _logger.LogWarning("Configuração da aplicação não encontrada no MongoDB durante o loop. Usando o último intervalo conhecido ({Interval} minutos).", currentSyncIntervalMinutes);
                    // Se não encontrar, continua com o último valor conhecido.
                }
            }

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
            
            _logger.LogInformation("⏰ Próxima verificação de janela/sincronização em {Interval} minutos.", currentSyncIntervalMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(currentSyncIntervalMinutes), stoppingToken).ConfigureAwait(false);
            }
            catch(TaskCanceledException)
            {
                 _logger.LogInformation("⏰ SyncSchedulerHostedService cancelado enquanto aguardava o próximo intervalo. O serviço está parando.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro inesperado durante o atraso do SyncSchedulerHostedService. O serviço pode continuar, mas o agendamento pode ser afetado.");
            }
        }
        _logger.LogInformation("⏰ SyncSchedulerHostedService está parando.");
    }
}