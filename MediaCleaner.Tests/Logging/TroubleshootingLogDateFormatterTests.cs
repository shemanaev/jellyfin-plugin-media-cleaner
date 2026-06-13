using System.IO;
using System.Xml.Serialization;
using MediaCleaner.Configuration;
using MediaCleaner.Controllers;
using MediaCleaner.Logging;
using Xunit;

namespace MediaCleaner.Tests.Logging;

public class TroubleshootingLogDateFormatterTests
{
    [Theory]
    [InlineData(TroubleshootingLogDateFormat.YYYYMMDD, "Collection played items for 1 users started at 20260613 17:04:02")]
    [InlineData(TroubleshootingLogDateFormat.DDMMYYYY, "Collection played items for 1 users started at 13/06/2026 17:04:02")]
    [InlineData(TroubleshootingLogDateFormat.MMDDYYYY, "Collection played items for 1 users started at 06/13/2026 17:04:02")]
    public void FormatLogDates_formats_date_and_preserves_time(
        TroubleshootingLogDateFormat dateFormat,
        string expected)
    {
        const string Line = "Collection played items for 1 users started at 06/13/2026 17:04:02";

        var result = TroubleshootingLogDateFormatter.FormatLogDates(Line, dateFormat);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatLogDates_leaves_lines_without_dates_unchanged()
    {
        const string Line = "Media cleanup task started. Dry run: True.";

        var result = TroubleshootingLogDateFormatter.FormatLogDates(
            Line,
            TroubleshootingLogDateFormat.YYYYMMDD);

        Assert.Equal(Line, result);
    }

    [Fact]
    public void New_configuration_defaults_to_yyyy_mm_dd()
    {
        var config = new PluginConfiguration();

        Assert.Equal(TroubleshootingLogDateFormat.YYYYMMDD, config.TroubleshootingLogDateFormat);
    }

    [Fact]
    public void Deserializing_legacy_configuration_uses_default_log_date_format()
    {
        const string Xml = """
            <PluginConfiguration>
              <KeepMoviesFor>7</KeepMoviesFor>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var reader = new StringReader(Xml);
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(TroubleshootingLogDateFormat.YYYYMMDD, config.TroubleshootingLogDateFormat);
    }

    [Fact]
    public void Resolve_log_date_format_uses_requested_format_over_config()
    {
        var result = TroubleshootingController.ResolveLogDateFormat(
            TroubleshootingLogDateFormat.DDMMYYYY,
            TroubleshootingLogDateFormat.YYYYMMDD);

        Assert.Equal(TroubleshootingLogDateFormat.DDMMYYYY, result);
    }

    [Fact]
    public void Resolve_log_date_format_uses_configured_format_without_request()
    {
        var result = TroubleshootingController.ResolveLogDateFormat(
            null,
            TroubleshootingLogDateFormat.MMDDYYYY);

        Assert.Equal(TroubleshootingLogDateFormat.MMDDYYYY, result);
    }
}
