using System.Collections.Generic;
using MediaCleaner.Configuration;
using Microsoft.Extensions.Logging;


namespace MediaCleaner.Logging;

internal class TroubleshootingLoggerConfiguration
{
    public List<string>? Output { get; set; }
    public LogLevel LogLevel { get; set; }
    public TroubleshootingLogDateFormat DateFormat { get; set; } = TroubleshootingLogDateFormat.YYYYMMDD;
}
