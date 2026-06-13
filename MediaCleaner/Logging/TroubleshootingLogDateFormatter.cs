using System.Globalization;
using System.Text.RegularExpressions;
using MediaCleaner.Configuration;

namespace MediaCleaner.Logging;

internal static partial class TroubleshootingLogDateFormatter
{
    public static string FormatLogDates(string message, TroubleshootingLogDateFormat dateFormat) =>
        LogDateTimeRegex().Replace(message, match =>
        {
            if (!int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
                !int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
                !int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
                month is < 1 or > 12 ||
                day is < 1 or > 31)
            {
                return match.Value;
            }

            var formattedDate = dateFormat switch
            {
                TroubleshootingLogDateFormat.DDMMYYYY => $"{day:D2}/{month:D2}/{year:D4}",
                TroubleshootingLogDateFormat.MMDDYYYY => $"{month:D2}/{day:D2}/{year:D4}",
                _ => $"{year:D4}{month:D2}{day:D2}",
            };

            return $"{formattedDate}{match.Groups["separator"].Value}{match.Groups["time"].Value}";
        });

    [GeneratedRegex(
        @"(?<!\d)(?<month>\d{1,2})/(?<day>\d{1,2})/(?<year>\d{4})(?<separator>\s+)(?<time>\d{1,2}:\d{2}:\d{2}(?:\s?[AP]M)?)(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LogDateTimeRegex();
}
