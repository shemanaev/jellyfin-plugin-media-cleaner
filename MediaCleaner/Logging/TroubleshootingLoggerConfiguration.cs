using System.Collections.Generic;
using Microsoft.Extensions.Logging;


namespace MediaCleaner.Logging;

internal class TroubleshootingLoggerConfiguration
{
    public List<string>? Output { get; set; }
    public LogLevel LogLevel { get; set; }
}
