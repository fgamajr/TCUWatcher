using System.Globalization;
using System.Text;

namespace TCUWatcher.API.Utils;

public static class StringUtils
{
    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

        for (int i = 0; i < normalizedString.Length; i++)
        {
            char c = normalizedString[i];
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool ContainsIgnoreCaseAndAccents(this string? source, string value)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(value))
            return false;

        string sourceWithoutDiacritics = RemoveDiacritics(source);
        string valueWithoutDiacritics = RemoveDiacritics(value);

        return sourceWithoutDiacritics.IndexOf(valueWithoutDiacritics, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
