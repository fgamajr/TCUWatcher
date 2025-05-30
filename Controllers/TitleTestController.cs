// In Controllers/TitleTestController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TCUWatcher.API.Models;
using TCUWatcher.API.Services;


[ApiController]
[Route("api/[controller]")]
public class TitleTestController : ControllerBase
{
    private readonly ITitleParserService _titleParserService;
    private readonly ILogger<TitleTestController> _logger;

    public TitleTestController(ITitleParserService titleParserService, ILogger<TitleTestController> logger)
    {
        _titleParserService = titleParserService;
        _logger = logger;
    }

    /// <summary>
    /// Parses a single title string.
    /// Pass the title as a query parameter, e.g., /api/TitleTest/ParseSingle?title=Your Encoded Title Here
    /// </summary>
    [HttpGet("ParseSingle")]
    public async Task<ActionResult<ParsedTitleDetails>> ParseSingleTitle([FromQuery] string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest("Title query parameter is required.");
        }
        _logger.LogInformation("Test parsing single title: \"{Title}\"", title);
        var result = await _titleParserService.ParseTitleAsync(title);
        if (result == null)
        {
            return NotFound("Parser returned null, check input or parser logic.");
        }
        return Ok(result);
    }

    /// <summary>
    /// Parses a list of title strings sent in the request body.
    /// POST a JSON array of strings, e.g., ["title1", "title2"]
    /// </summary>
    [HttpPost("ParseBatch")]
    // public async Task<ActionResult<List<ParsedTitleDetails>>> ParseBatchTitles([FromBody] List<string> titles)
    public async Task<ActionResult<List<ParsedTitleDetails>>> ParseBatchTitles([FromBody] List<TitleInput> titleInputs)
    {
        if (titleInputs == null || !titleInputs.Any()) // Use titleInputs
        {
            return BadRequest("A list of titles is required in the request body.");
        }

        _logger.LogInformation("Test parsing batch of {Count} titles.", titleInputs.Count); // Use titleInputs
        var results = new List<ParsedTitleDetails>();
        foreach (var titleInput in titleInputs) // Use titleInputs
        {
            if (string.IsNullOrWhiteSpace(titleInput.Title))
            {
                results.Add(new ParsedTitleDetails(titleInput.Title ?? "NULL_INPUT") { WasSuccessfullyParsed = false, ParsingErrors = { "Input title was null or empty."} });
                continue;
            }
            var parsed = await _titleParserService.ParseTitleAsync(titleInput.Title);
            results.Add(parsed ?? new ParsedTitleDetails(titleInput.Title) { WasSuccessfullyParsed = false, ParsingErrors = { "Parser returned null." } });
        }
        return Ok(results);
    }
}