using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
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

internal enum WebClientInjectionMethod
{
    None,
    FileTransformation,
    JavaScriptInjector,
    StartupFilter,
    FileReplacement,
}

internal sealed record WebClientInjectionStatus(
    WebClientInjectionState State,
    WebClientInjectionMethod Method,
    string IndexPath,
    bool Changed,
    string? Error = null);

internal sealed class LeavingSoonWebClientInstaller
{
    private delegate bool TextMutation(string content, out string updated);

    internal const string ScriptStart = "<!-- Media Cleaner Leaving Soon item menu -->";
    internal const string ScriptEnd = "<!-- /Media Cleaner Leaving Soon item menu -->";
    internal const string ScriptElementId = "mediaCleanerLeavingSoonItemMenuScript";
    internal const string JavaScriptRegistrationId = "607fee77-97eb-41fe-bf22-26844d99ffb0-leaving-soon-item-menu";
    internal static readonly Guid TransformationId = Guid.Parse("5481f106-c9e1-4d74-8c6d-05da93d76c57");
    internal static string ScriptUrl => $"./ConfigurationPage?name=MediaCleaner_LeavingSoonItemMenu_js&v={typeof(LeavingSoonWebClientInstaller).Assembly.ManifestModule.ModuleVersionId:N}";
    internal static string ScriptTag => $"<script id=\"{ScriptElementId}\" type=\"module\" src=\"{ScriptUrl}\"></script>";

    private readonly string _indexPath;
    private readonly ILogger _logger;
    private volatile bool _startupFilterAvailable;
    private volatile bool _removed;
    private volatile WebClientInjectionStatus _status;

    internal static LeavingSoonWebClientInstaller? Current { get; private set; }
    internal WebClientInjectionStatus Status => _status;
    internal bool ShouldUseStartupFilter => _status.Method == WebClientInjectionMethod.StartupFilter;

    public LeavingSoonWebClientInstaller(
        IApplicationPaths applicationPaths,
        ILogger<LeavingSoonWebClientInstaller> logger)
        : this(applicationPaths, (ILogger)logger)
    {
    }

    internal LeavingSoonWebClientInstaller(IApplicationPaths applicationPaths, ILogger logger)
    {
        _indexPath = Path.Combine(applicationPaths.WebPath, "index.html");
        _logger = logger;
        _status = new WebClientInjectionStatus(
            WebClientInjectionState.NotChecked,
            WebClientInjectionMethod.None,
            _indexPath,
            false);
        Current = this;
    }

    internal void MarkStartupFilterAvailable() => _startupFilterAvailable = true;

    public void Install()
    {
        _removed = false;
        if (_status.Method != WebClientInjectionMethod.FileReplacement)
            RemoveLegacyFileInjection();
        if (TryInstallExternal())
            return;

        if (_startupFilterAvailable)
        {
            _status = new WebClientInjectionStatus(
                WebClientInjectionState.Installed,
                WebClientInjectionMethod.StartupFilter,
                _indexPath,
                true);
            _logger.LogInformation("Leaving Soon web-client script will be injected through an ASP.NET startup filter.");
            return;
        }

        _status = UpdateIndex(
            EnsureScriptInstalled,
            "installed",
            WebClientInjectionState.Installed,
            WebClientInjectionMethod.FileReplacement);
    }

    internal bool TryUpgradeToExternal()
    {
        if (_removed)
            return true;

        if (_status.Method is WebClientInjectionMethod.FileTransformation or WebClientInjectionMethod.JavaScriptInjector)
            return true;

        if (!TryInstallExternal())
            return false;

        RemoveLegacyFileInjection();
        return true;
    }

    public void Remove()
    {
        _removed = true;
        var activeMethod = _status.Method;
        _status = new WebClientInjectionStatus(
            WebClientInjectionState.NotChecked,
            WebClientInjectionMethod.None,
            _indexPath,
            false);

        if (activeMethod == WebClientInjectionMethod.FileTransformation)
            TryInvokeRemoval("Jellyfin.Plugin.FileTransformation", "RemoveTransformation", TransformationId);
        else if (activeMethod == WebClientInjectionMethod.JavaScriptInjector)
            TryInvokeRemoval("Jellyfin.Plugin.JavaScriptInjector", "UnregisterScript", JavaScriptRegistrationId);

        UpdateIndex(
            EnsureScriptRemoved,
            "removed",
            WebClientInjectionState.NotChecked,
            WebClientInjectionMethod.None,
            logMissingIndex: false);
    }

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
        $"<script id=\"{ScriptElementId}\" type=\"module\" src=\"./ConfigurationPage?name=MediaCleaner_LeavingSoonItemMenu_js&v={moduleVersionId:N}\"></script>";

    private bool TryInstallExternal()
    {
        if (TryRegisterJavaScriptInjector())
        {
            _status = new WebClientInjectionStatus(
                WebClientInjectionState.Installed,
                WebClientInjectionMethod.JavaScriptInjector,
                _indexPath,
                true);
            return true;
        }

        if (TryRegisterFileTransformation())
        {
            _status = new WebClientInjectionStatus(
                WebClientInjectionState.Installed,
                WebClientInjectionMethod.FileTransformation,
                _indexPath,
                true);
            return true;
        }

        return false;
    }

    private bool TryRegisterFileTransformation()
    {
        var pluginInterface = FindPluginInterface(
            "Jellyfin.Plugin.FileTransformation",
            "Jellyfin.Plugin.FileTransformation.PluginInterface");
        var registerMethod = pluginInterface?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
        if (registerMethod is null)
            return false;

        try
        {
            var payload = BuildPluginPayload(registerMethod, new
            {
                id = TransformationId.ToString("D", CultureInfo.InvariantCulture),
                fileNamePattern = "index\\.html",
                callbackAssembly = typeof(LeavingSoonFileTransformation).Assembly.FullName,
                callbackClass = typeof(LeavingSoonFileTransformation).FullName,
                callbackMethod = nameof(LeavingSoonFileTransformation.Transform),
            });
            registerMethod.Invoke(null, new[] { payload });
            _logger.LogInformation("Leaving Soon web-client script registered with File Transformation.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "File Transformation is present but not ready for Media Cleaner registration.");
            return false;
        }
    }

    private bool TryRegisterJavaScriptInjector()
    {
        var pluginInterface = FindPluginInterface(
            "Jellyfin.Plugin.JavaScriptInjector",
            "Jellyfin.Plugin.JavaScriptInjector.PluginInterface");
        var registerMethod = pluginInterface?.GetMethod("RegisterScript", BindingFlags.Public | BindingFlags.Static);
        if (registerMethod is null)
            return false;

        try
        {
            var loader = $$"""
                (() => {
                    if (document.getElementById('{{ScriptElementId}}')) return;
                    const script = document.createElement('script');
                    script.id = '{{ScriptElementId}}';
                    script.type = 'module';
                    script.src = new URL('{{ScriptUrl}}', document.baseURI).href;
                    document.head.appendChild(script);
                })();
                """;
            var payload = BuildPluginPayload(registerMethod, new
            {
                id = JavaScriptRegistrationId,
                name = "Media Cleaner — Leaving Soon item menu",
                script = loader,
                enabled = true,
                requiresAuthentication = false,
                pluginId = "607fee77-97eb-41fe-bf22-26844d99ffb0",
                pluginName = "Media Cleaner",
                pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            });
            var result = registerMethod.Invoke(null, new[] { payload });
            if (result is not true)
                return false;

            _logger.LogInformation("Leaving Soon web-client script registered with JavaScript Injector.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JavaScript Injector is present but not ready for Media Cleaner registration.");
            return false;
        }
    }

    private static Type? FindPluginInterface(string assemblyName, string typeName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            ?.GetType(typeName, throwOnError: false);

    private static object BuildPluginPayload(MethodInfo registerMethod, object values)
    {
        var payloadType = registerMethod.GetParameters().SingleOrDefault()?.ParameterType
            ?? throw new InvalidOperationException($"{registerMethod.Name} does not have exactly one payload parameter.");
        var parseMethod = payloadType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null)
            ?? throw new InvalidOperationException($"Cannot construct plugin payload type '{payloadType.FullName}'.");
        var json = JsonSerializer.Serialize(values);
        return parseMethod.Invoke(null, new object[] { json })
            ?? throw new InvalidOperationException("Plugin payload parser returned null.");
    }

    private void TryInvokeRemoval(string assemblyName, string methodName, object identifier)
    {
        try
        {
            var pluginInterface = FindPluginInterface(assemblyName, $"{assemblyName}.PluginInterface");
            pluginInterface?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { identifier });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove Leaving Soon registration through {PluginAssembly}.", assemblyName);
        }
    }

    private void RemoveLegacyFileInjection()
    {
        if (!File.Exists(_indexPath))
            return;

        try
        {
            var content = File.ReadAllText(_indexPath);
            if (!EnsureScriptRemoved(content, out var updated))
                return;

            AtomicWrite(_indexPath, updated);
            _logger.LogInformation("Removed the legacy Leaving Soon script block from {Path}.", _indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove a legacy Leaving Soon script block from {Path}.", _indexPath);
        }
    }

    private WebClientInjectionStatus UpdateIndex(
        TextMutation mutation,
        string action,
        WebClientInjectionState successState,
        WebClientInjectionMethod successMethod,
        bool logMissingIndex = true)
    {
        if (!File.Exists(_indexPath))
        {
            if (logMissingIndex)
            {
                _logger.LogWarning(
                    "Cannot add the Leaving Soon item action because Jellyfin web index was not found at {Path}.",
                    _indexPath);
            }

            return new WebClientInjectionStatus(
                WebClientInjectionState.MissingWebIndex,
                WebClientInjectionMethod.None,
                _indexPath,
                false,
                "Jellyfin web index was not found.");
        }

        try
        {
            var content = File.ReadAllText(_indexPath);
            if (!mutation(content, out var updated))
                return new WebClientInjectionStatus(successState, successMethod, _indexPath, false);

            AtomicWrite(_indexPath, updated);
            _logger.LogInformation("Leaving Soon Jellyfin Web item action was {Action} in {Path}.", action, _indexPath);
            return new WebClientInjectionStatus(successState, successMethod, _indexPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the Leaving Soon item action in {Path}.", _indexPath);
            return new WebClientInjectionStatus(
                WebClientInjectionState.Failed,
                WebClientInjectionMethod.None,
                _indexPath,
                false,
                ex.Message);
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

/// <summary>Payload created by File Transformation for a registered callback.</summary>
public sealed class LeavingSoonFileTransformationPayload
{
    public string? Contents { get; set; }
}

/// <summary>Reflection callback used by the optional File Transformation plugin.</summary>
public static class LeavingSoonFileTransformation
{
    public static string Transform(LeavingSoonFileTransformationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        LeavingSoonWebClientInstaller.EnsureScriptInstalled(payload.Contents ?? string.Empty, out var updated);
        return updated;
    }
}
