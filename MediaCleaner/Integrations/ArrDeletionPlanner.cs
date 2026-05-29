using Jellyfin.Data.Enums;

namespace MediaCleaner.Integrations;

internal static class ArrDeletionPlanner
{
    public static bool CanDelegate(BaseItemKind kind) =>
        kind is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode;
}
