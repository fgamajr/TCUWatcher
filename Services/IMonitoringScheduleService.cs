
namespace TCUWatcher.API.Services;

public interface IMonitoringScheduleService
{
    Task<bool> IsCurrentlyInMonitoringWindowAsync();
}