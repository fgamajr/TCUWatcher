using System.Globalization;

namespace TCUWatcher.API.Utils;

public static class DateTimeUtils
{
    private static TimeZoneInfo? _brtTimeZone;

    private static TimeZoneInfo GetBrtTimeZone(IConfiguration configuration)
    {
        if (_brtTimeZone == null)
        {
            var timeZoneId = configuration.GetValue<string>("MonitoringHours:TimeZone") ?? "America/Sao_Paulo";
            try
            {
                _brtTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                Console.WriteLine($"Warning: Time zone ID '{timeZoneId}' not found. Trying Windows ID 'E. South America Standard Time'.");
                try
                {
                    _brtTimeZone = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
                }
                catch (TimeZoneNotFoundException)
                {
                     Console.WriteLine($"Warning: Time zone ID 'E. South America Standard Time' also not found. Falling back to UTC-3.");
                    _brtTimeZone = TimeZoneInfo.CreateCustomTimeZone("BRT-Fallback", TimeSpan.FromHours(-3), "BRT-Fallback", "BRT-Fallback");
                }
            }
        }
        return _brtTimeZone;
    }

    public static string FormatDateTime(DateTime dt, IConfiguration configuration)
    {
        var brtZone = GetBrtTimeZone(configuration);
        // Garante que dt seja UTC antes de converter
        DateTime utcDt = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : TimeZoneInfo.ConvertTimeToUtc(dt);
        DateTime brtDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDt, brtZone);
        return brtDateTime.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    public static DateTime GetBrazilianDateTimeNow(IConfiguration configuration)
    {
        var brtZone = GetBrtTimeZone(configuration);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, brtZone);
    }
}
