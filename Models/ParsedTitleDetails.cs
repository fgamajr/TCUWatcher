using System;
using System.Collections.Generic;

namespace TCUWatcher.API.Models
{
        public class TitleInput // <<<< ENSURE THIS CLASS DEFINITION IS PRESENT
    {
        public string? Title { get; set; }
    }


    public class ParsedTitleDetails
    {
        public string? OriginalTitle { get; set; }
        public string? Colegiate { get; set; }     // e.g., "Plenário", "1ª Câmara"
        public string? SessionType { get; set; }   // e.g., "Ordinária", "Extraordinária"
        public DateTime? SessionDate { get; set; } // The extracted date
        public bool WasSuccessfullyParsed { get; set; } = false;
        public List<string> ParsingErrors { get; private set; } = new List<string>();

        public ParsedTitleDetails(string originalTitle)
        {
            OriginalTitle = originalTitle;
        }

        public void AddError(string error)
        {
            ParsingErrors.Add(error);
            WasSuccessfullyParsed = false; // Ensure it's false if any errors are added
        }
    }
}