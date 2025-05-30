using System.Threading.Tasks;
using TCUWatcher.API.Models;

namespace TCUWatcher.API.Services
{
    public interface ITitleParserService
    {
        /// <summary>
        /// Parses a raw video title to extract structured details.
        /// </summary>
        /// <param name="rawTitle">The raw video title string.</param>
        /// <returns>A ParsedTitleDetails object. Returns null if the input title is invalid,
        /// or a ParsedTitleDetails object with WasSuccessfullyParsed = false if parsing failed
        /// but some details might still be partially extracted or errors logged.</returns>
        Task<ParsedTitleDetails?> ParseTitleAsync(string? rawTitle);
    }
}