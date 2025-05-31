using System.Collections.Generic;
using System.Threading.Tasks;

namespace TCUWatcher.API.Services
{
    public interface IOcrProcessExtractorService
    {
        /// <summary>
        /// Extracts validated TCU process numbers from a given image file.
        /// </summary>
        /// <param name="imagePath">The full local path to the image file (snapshot).</param>
        /// <param name="liveEventIdForLogging">Identifier for logging context.</param>
        /// <returns>A list of validated process number strings (e.g., "TC 006.226/2017-5"). Returns an empty list if none are found or in case of error.</returns>
        Task<List<string>> ExtractValidatedProcessNumbersAsync(string imagePath, string liveEventIdForLogging);
    }
}