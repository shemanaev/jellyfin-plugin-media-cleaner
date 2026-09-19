using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.LeavingSoon;

internal enum WebClientInjectionState
{
    NotChecked,
    Installed,
    MissingWebIndex,
    Failed,
}

internal sealed record WebClientInjectionStatus(
    WebClientInjectionState State,
    string IndexPath,
    bool Changed,
    string? Error = null);

internal sealed class LeavingSoonWebClientInstaller
{
    private delegate bool TextMutation(string content, out string updated);

    internal const string ScriptStart = "<!-- Media Cleaner Leaving Soon item menu -->";
    internal const string ScriptEnd = "<!-- /Media Cleaner Leaving Soon item menu -->";
    internal static string ScriptTag => CreateScriptTag(typeof(LeavingSoonWebClientInstaller).Assembly.ManifestModule.ModuleVersionId);

    private readonly string _indexPath;
    private readonly ILogger _logger;

    internal WebClientInjectionStatus Status { get; private set; }

    public LeavingSoonWebClientInstaller(IApplicationPaths applicationPaths, ILogger logger)
    {
        _indexPath = Path.Combine(applicationPaths.WebPath, "index.html");
        _logger = logger;
        Status = new WebClientInjectionStatus(WebClientInjectionState.NotChecked, _indexPath, false);
    }

    public void Install() => Status = UpdateIndex(EnsureScriptInstalled, "installed", WebClientInjectionState.Installed);

    public void Remove() => UpdateIndex(EnsureScriptRemoved, "removed", WebClientInjectionState.NotChecked);

    internal static bool EnsureScriptInstalled(string content, out string updated)
    {
        var block = $"{ScriptStart}\n{ScriptTag}\n{ScriptEnd}";
        var withoutOwnedBlocks = RemoveScriptBlocks(content);
        var closingBody = withoutOwnedBlocks.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closingBody < 0)
            throw new InvalidDataException("Jellyfin web index does not contain a closing body element.");

        var prefix = closingBody > 0 && withoutOwnedBlocks[closingBody - 1] == '\n' ? string.Empty : "\n";
        updated = withoutOwnedBlocks.Insert(closingBody, $"{prefix}{block}\n");
        return !string.Equals(content, updated, StringComparison.Ordinal);
    }

    internal static bool EnsureScriptRemoved(string content, out string updated)
    {
        updated = RemoveScriptBlocks(content);
        return !string.Equals(content, updated, StringComparison.Ordinal);
    }

    internal static string CreateScriptTag(Guid moduleVersionId) =>
        $"<script type=\"module\" src=\"./ConfigurationPage?name=MediaCleaner_LeavingSoonItemMenu_js&v={moduleVersionId:N}\"></script>";

    private WebClientInjectionStatus UpdateIndex(TextMutation mutation, string action, WebClientInjectionState successState)
    {
        if (!File.Exists(_indexPath))
        {
            _logger.LogWarning(
                "Cannot add the Leaving Soon item action because Jellyfin web index was not found at {Path}. " +
                "The native collection remains available, but the item action requires a web-client injection.",
                _indexPath);
            return new WebClientInjectionStatus(
                WebClientInjectionState.MissingWebIndex,
                _indexPath,
                false,
                "Jellyfin web index was not found.");
        }

        try
        {
            var content = File.ReadAllText(_indexPath);
            if (!mutation(content, out var updated))
                return new WebClientInjectionStatus(successState, _indexPath, false);

            AtomicWrite(_indexPath, updated);
            _logger.LogInformation("Leaving Soon Jellyfin Web item action was {Action} in {Path}.", action, _indexPath);
            return new WebClientInjectionStatus(successState, _indexPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not update the Leaving Soon item action in {Path}. " +
                "The native collection remains available, but this web root is read-only or belongs to a separate web client.",
                _indexPath);
            return new WebClientInjectionStatus(WebClientInjectionState.Failed, _indexPath, false, ex.Message);
        }
    }

    private static void AtomicWrite(string targetPath, string content)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException($"Path '{targetPath}' has no parent directory.");
        var temporaryPath = Path.Combine(directory, $".mediacleaner-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string RemoveScriptBlocks(string content)
    {
        while (true)
        {
            var start = content.IndexOf(ScriptStart, StringComparison.Ordinal);
            if (start < 0)
                return content;
            var end = content.IndexOf(ScriptEnd, start + ScriptStart.Length, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidDataException("Jellyfin web index contains an incomplete Media Cleaner item-action block.");

            end += ScriptEnd.Length;
            if (end < content.Length && content[end] == '\r') end++;
            if (end < content.Length && content[end] == '\n') end++;
            content = content.Remove(start, end - start);
        }
    }
}
