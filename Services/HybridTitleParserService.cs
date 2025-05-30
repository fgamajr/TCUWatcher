using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TCUWatcher.API.Models; // Assuming ParsedTitleDetails is here

namespace TCUWatcher.API.Services
{
    public class HybridTitleParserService : ITitleParserService
    {
        private readonly ILogger<HybridTitleParserService> _logger;

        // Canonical keywords (KEYS should be normalized: lowercase, no accents)
        // VALUES are the desired display/storage format.
        private static readonly Dictionary<string, string> CanonicalColegiates = new()
        {
            { "1a camara", "1ª Câmara" }, 
            { "primeira camara", "1ª Câmara" },
            { "2a camara", "2ª Câmara" }, 
            { "segunda camara", "2ª Câmara" },
            { "plenario", "Plenário" }
            // Add other known colegiates and their normalized versions (e.g., "turma recursal")
        };

        private static readonly Dictionary<string, string> CanonicalSessionTypes = new()
        {
            { "ordinaria", "Ordinária" },
            { "extraordinaria", "Extraordinária" },
            { "solene", "Solene" } // Example
            // Add other known session types and their normalized versions (e.g., "virtual")
        };

        private const int DefaultMaxLevenshteinDistance = 2; 
        private const int NgramMinLength = 1; 
        private const int NgramMaxLength = 3; // Max words in a phrase to check (e.g., "sessao da 1a camara")

        public HybridTitleParserService(ILogger<HybridTitleParserService> logger)
        {
            _logger = logger;
        }

        public Task<ParsedTitleDetails?> ParseTitleAsync(string? rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                _logger.LogWarning("Input title is null or whitespace. Cannot parse.");
                return Task.FromResult<ParsedTitleDetails?>(null);
            }

            var result = new ParsedTitleDetails(rawTitle);
            string remainingTitleForKeywords = rawTitle; // Will be modified after date extraction
            _logger.LogDebug("Attempting to parse title: \"{RawTitle}\"", rawTitle);

            // 1. Extract and Remove Date
            var datePatternsAndFormats = new[]
            {
                new { Pattern = @"\b(\d{1,2}\s+de\s+([a-zA-ZçÇãÃõÕÁÉÍÓÚáéíóú]+)\s+de\s+\d{4})\b", Format = "d 'de' MMMM 'de' yyyy", Culture = new CultureInfo("pt-BR"), IsFullMatchGroup = true },
                new { Pattern = @"\b(\d{1,2}/\d{1,2}/\d{4})\b", Format = "d/M/yyyy", Culture = CultureInfo.InvariantCulture, IsFullMatchGroup = true },
                new { Pattern = @"\b(\d{1,2}-\d{1,2}-\d{4})\b", Format = "d-M-yyyy", Culture = CultureInfo.InvariantCulture, IsFullMatchGroup = true },
                new { Pattern = @"\b(\d{4}/\d{1,2}/\d{1,2})\b", Format = "yyyy/M/d", Culture = CultureInfo.InvariantCulture, IsFullMatchGroup = true },
                new { Pattern = @"\b(\d{4}-\d{1,2}-\d{1,2})\b", Format = "yyyy-M-d", Culture = CultureInfo.InvariantCulture, IsFullMatchGroup = true }
            };

            Match dateMatchResult = System.Text.RegularExpressions.Match.Empty;
            string? matchedDateFormatString = null;
            CultureInfo? matchedCultureToUse = null;
            string matchedDateText = string.Empty;

            foreach (var paf in datePatternsAndFormats)
            {
                var currentMatch = Regex.Match(remainingTitleForKeywords, paf.Pattern, RegexOptions.IgnoreCase);
                if (currentMatch.Success)
                {
                    matchedDateText = paf.IsFullMatchGroup ? currentMatch.Value : currentMatch.Groups[1].Value;
                    dateMatchResult = currentMatch;
                    matchedDateFormatString = paf.Format;
                    matchedCultureToUse = paf.Culture;
                    _logger.LogDebug("Date pattern '{Pattern}' matched '{MatchedText}' in title part: \"{RemainingTitleForKeywords}\"", paf.Pattern, matchedDateText, remainingTitleForKeywords);
                    break; 
                }
            }

            if (dateMatchResult.Success && !string.IsNullOrEmpty(matchedDateFormatString) && matchedCultureToUse != null)
            {
                if (DateTime.TryParseExact(matchedDateText, matchedDateFormatString, matchedCultureToUse, DateTimeStyles.None, out DateTime sessionDate))
                {
                    result.SessionDate = DateTime.SpecifyKind(sessionDate.Date, DateTimeKind.Utc); 
                    string patternToRemoveDate = @"\b" + Regex.Escape(matchedDateText) + @"\b";
                    remainingTitleForKeywords = Regex.Replace(remainingTitleForKeywords, patternToRemoveDate, "", RegexOptions.IgnoreCase).Trim();
                    _logger.LogDebug("Extracted Date: {Date}, Format: '{Format}'. Remaining title for keyword search: \"{RemainingTitle}\"", 
                        result.SessionDate, matchedDateFormatString, remainingTitleForKeywords);
                }
                else
                {
                    result.AddError($"Could not parse extracted date string '{matchedDateText}' with format '{matchedDateFormatString}'.");
                    _logger.LogWarning("Could not parse extracted date string '{DateValue}' with format '{Format}' from title '{OriginalTitle}'", 
                        matchedDateText, matchedDateFormatString, rawTitle);
                }
            }
            else
            {
                result.AddError("Date in a recognized format not found in title.");
                _logger.LogDebug("Date not found in title '{OriginalTitle}' with any of the specified patterns.", rawTitle);
            }

            // 2. Normalize the remaining title part for keyword extraction
            string normalizedTitleForKeywords = RemoveDiacritics(remainingTitleForKeywords.ToLowerInvariant());
            normalizedTitleForKeywords = Regex.Replace(normalizedTitleForKeywords, @"\s*-\s*", " ").Trim(); 
            normalizedTitleForKeywords = Regex.Replace(normalizedTitleForKeywords, @"\s+", " ").Trim(); 

            _logger.LogDebug("Normalized title for keyword search: \"{NormalizedTitle}\"", normalizedTitleForKeywords);

            string tempWorkTitle = normalizedTitleForKeywords; 

            // 3. Extract Colegiate
            var (colegiate, matchedColegiatePhrase, _) = FindBestFuzzyMatch(
                tempWorkTitle, 
                CanonicalColegiates, 
                DefaultMaxLevenshteinDistance
            );

            if (colegiate != null && matchedColegiatePhrase != null)
            {
                result.Colegiate = colegiate;
                string patternToRemove = @"\b" + Regex.Escape(matchedColegiatePhrase) + @"\b";
                tempWorkTitle = Regex.Replace(tempWorkTitle, patternToRemove, "", RegexOptions.IgnoreCase).Trim();
                tempWorkTitle = Regex.Replace(tempWorkTitle, @"\s+", " ").Trim(); 
                _logger.LogDebug("Found Colegiate: {Colegiate}. Remaining title for session type: \"{RemainingTitleForSessionType}\"", colegiate, tempWorkTitle);
            } else {
                result.AddError("Colegiate not identified.");
                 _logger.LogDebug("Colegiate not identified in processed title part: \"{ProcessedTitlePart}\"", normalizedTitleForKeywords);
            }
            
            // 4. Extract Session Type from the (potentially modified) normalized title
            var (sessionType, matchedSessionTypePhrase, _) = FindBestFuzzyMatch( // Use matchedSessionTypePhrase if needed for further removal
                tempWorkTitle, 
                CanonicalSessionTypes, 
                DefaultMaxLevenshteinDistance
            );

            if (sessionType != null)
            {
                result.SessionType = sessionType;
                 _logger.LogDebug("Found Session Type: {SessionType} from remaining text \"{RemainingText}\"", sessionType, tempWorkTitle);
                // Optionally remove matchedSessionTypePhrase from tempWorkTitle if more parsing stages were to follow
            } else {
                result.AddError("Session Type not identified.");
                _logger.LogDebug("Session Type not identified in remaining processed title part: \"{RemainingText}\"", tempWorkTitle);
            }

            // 5. Determine overall success
            bool dateFound = result.SessionDate.HasValue;
            bool colegiateFound = !string.IsNullOrEmpty(result.Colegiate);
            bool typeFound = !string.IsNullOrEmpty(result.SessionType);

            if (dateFound && (colegiateFound || typeFound)) // Success if date and at least one other part is found
            {
                result.WasSuccessfullyParsed = true;
                // Clean up non-critical errors if major components were found
                if (colegiateFound && typeFound) { /* Both found, good */ }
                else if (colegiateFound) { result.ParsingErrors.Remove("Session Type not identified."); }
                else if (typeFound) { result.ParsingErrors.Remove("Colegiate not identified."); }
            } else {
                 result.WasSuccessfullyParsed = false; 
            }
            
            // Consolidate final success based on errors
            if (result.ParsingErrors.Any(e => e.Contains("Date in a recognized format not found"))) {
                result.WasSuccessfullyParsed = false;
            }
            if (result.WasSuccessfullyParsed && result.ParsingErrors.Any()){ // If marked success but still has errors, clear them if they are minor ones we decided to ignore.
                 if (result.ParsingErrors.All(e => e.EndsWith("not identified."))) { // Only clear "not identified" if major parts were found
                    if ((colegiateFound || typeFound) && dateFound) result.ParsingErrors.Clear();
                 }
            }


            if (!result.WasSuccessfullyParsed) {
                _logger.LogWarning("Failed to fully parse title '{RawTitle}'. Date: {Date}, Colegiate: {Colegiate}, Type: {Type}. Errors: [{Errors}]", 
                    rawTitle, result.SessionDate?.ToString("dd/MM/yyyy") ?? "N/A", result.Colegiate ?? "N/A", result.SessionType ?? "N/A", string.Join("; ", result.ParsingErrors));
            } else {
                 _logger.LogInformation("Successfully parsed title '{RawTitle}'. Date: {Date}, Colegiate: {Colegiate}, Type: {Type}.", 
                    rawTitle, result.SessionDate?.ToString("dd/MM/yyyy"), result.Colegiate, result.SessionType);
            }

            return Task.FromResult<ParsedTitleDetails?>(result);
        }

        private List<string> GenerateNgrams(string inputText, int minN, int maxN)
        {
            var words = inputText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ngrams = new List<string>();
            if (words.Length == 0) return ngrams;

            for (int n = minN; n <= Math.Min(maxN, words.Length); n++)
            {
                for (int i = 0; i <= words.Length - n; i++)
                {
                    ngrams.Add(string.Join(" ", words.Skip(i).Take(n)));
                }
            }
            return ngrams.Distinct().OrderByDescending(s => s.Length).ToList();
        }
        
        private (string? canonicalValue, string? matchedInputPhrase, int distance) FindBestFuzzyMatch(
            string normalizedInputTextToSearch,
            Dictionary<string, string> canonicalItems,
            int maxDistanceThreshold)
        {
            if (string.IsNullOrWhiteSpace(normalizedInputTextToSearch))
            {
                return (null, null, maxDistanceThreshold + 1);
            }

            var inputNgrams = GenerateNgrams(normalizedInputTextToSearch, NgramMinLength, NgramMaxLength);
            if (!inputNgrams.Any()) return (null, null, maxDistanceThreshold + 1);

            string? bestCanonicalValue = null;
            string? bestMatchedInputPhrase = null;
            int bestOverallDistance = maxDistanceThreshold + 1;

            foreach (var canonicalEntry in canonicalItems.OrderByDescending(pair => pair.Key.Length)) 
            {
                string normalizedCanonicalKey = canonicalEntry.Key;
                string displayValue = canonicalEntry.Value;

                foreach (var inputNgram in inputNgrams) 
                {
                    int distance = CalculateLevenshteinDistance(inputNgram, normalizedCanonicalKey);

                    if (distance < bestOverallDistance && distance <= maxDistanceThreshold)
                    {
                        bestOverallDistance = distance;
                        bestCanonicalValue = displayValue;
                        bestMatchedInputPhrase = inputNgram; 
                    }
                    if (distance == 0 && inputNgram == normalizedCanonicalKey) 
                    {
                        _logger.LogTrace("Perfect fuzzy match: Input N-gram '{InputNgram}' to Canonical Key '{CanonicalKey}' for '{DisplayValue}'", inputNgram, normalizedCanonicalKey, displayValue);
                        return (displayValue, inputNgram, 0); // Return immediately on perfect match for this canonical key
                    }
                }
                // If a very good match (e.g., distance 0 or 1) was found for this *specific canonicalEntry*
                // after checking all its ngrams, we might consider it good enough and break from the outer loop.
                // However, this could prevent finding an even better match for a *different* (perhaps shorter) canonical entry.
                // The current logic continues to find the *absolute best* match across all canonical entries.
            }
            
            if (bestCanonicalValue != null) {
                 _logger.LogTrace("Best overall fuzzy match found: Input Phrase '{BestMatchedInputPhrase}' (dist: {Distance}) maps to Canonical Value '{BestCanonicalValue}'", bestMatchedInputPhrase, bestOverallDistance, bestCanonicalValue);
                return (bestCanonicalValue, bestMatchedInputPhrase, bestOverallDistance);
            }

            return (null, null, maxDistanceThreshold + 1);
        }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);
            foreach (var c in normalizedString) 
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }
            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static int CalculateLevenshteinDistance(string source, string target)
        {
            if (string.IsNullOrEmpty(source)) return string.IsNullOrEmpty(target) ? 0 : target.Length;
            if (string.IsNullOrEmpty(target)) return source.Length;
            if (source == target) return 0;

            int n = source.Length;
            int m = target.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (target[j - 1] == source[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }
    }
}