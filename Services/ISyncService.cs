using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.Services;

public interface ISyncService
{
    Task FullSyncLivesAsync();
}
