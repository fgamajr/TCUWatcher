using TCUWatcher.API.Models;
using System.Collections.Generic; // Para List
using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.Services;

public interface IYouTubeService
{
    Task<List<string>> CheckAndStoreLiveAsync(string channelId);
}
