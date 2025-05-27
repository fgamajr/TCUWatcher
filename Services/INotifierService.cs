using TCUWatcher.API.Models;
using System.Collections.Generic; // Para Dictionary
using System.Threading.Tasks; // Para Task

namespace TCUWatcher.API.Services;

public interface INotifierService
{
    Task SendWebhookAsync(LiveEvent eventData);
    Task SendWebhookAsync(Dictionary<string, string> eventData);
}
