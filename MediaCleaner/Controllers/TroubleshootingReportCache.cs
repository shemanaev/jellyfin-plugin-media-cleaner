using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCleaner.Controllers;

internal sealed class TroubleshootingReportCache(TimeSpan lifetime, int maxReports)
{
    private readonly object sync = new();
    private readonly Dictionary<string, CachedTroubleshootingReport> reports = new(StringComparer.Ordinal);

    public bool TryGet(string? reportId, DateTime utcNow, out CachedTroubleshootingReport report)
    {
        lock (sync)
        {
            RemoveExpired(utcNow);
            if (!string.IsNullOrWhiteSpace(reportId)
                && reports.TryGetValue(reportId, out report!))
            {
                return true;
            }

            report = null!;
            return false;
        }
    }

    public void Set(CachedTroubleshootingReport report, DateTime utcNow)
    {
        lock (sync)
        {
            RemoveExpired(utcNow);
            reports[report.ReportId] = report;

            while (reports.Count > maxReports)
            {
                var oldest = reports.Values.MinBy(x => x.CreatedUtc);
                if (oldest is null)
                {
                    break;
                }

                reports.Remove(oldest.ReportId);
            }
        }
    }

    private void RemoveExpired(DateTime utcNow)
    {
        var cutoff = utcNow - lifetime;
        foreach (var reportId in reports
            .Where(x => x.Value.CreatedUtc < cutoff)
            .Select(x => x.Key)
            .ToList())
        {
            reports.Remove(reportId);
        }
    }
}
