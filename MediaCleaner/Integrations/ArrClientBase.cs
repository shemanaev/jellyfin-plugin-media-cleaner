using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaCleaner.Configuration;

namespace MediaCleaner.Integrations;

internal abstract class ArrClientBase
{
    protected static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;

    protected ArrClientBase(HttpClient httpClient, ConfigArrInstanceNode config)
    {
        _httpClient = httpClient;
        Config = config;
        _httpClient.Timeout = Config.Timeout;
    }

    protected ConfigArrInstanceNode Config { get; }

    public bool IsConfigured => Config.IsConfigured;

    public async Task<ArrConnectionResult> TestConnectionAsync(string serviceName, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new(false, $"{serviceName} is not fully configured.");
        }

        try
        {
            var status = await GetJsonAsync<ArrSystemStatusResource>("system/status", null, cancellationToken);
            return new(true, $"{serviceName} connection successful.", status?.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, $"{serviceName} connection failed: {ex.Message}");
        }
    }

    protected async Task<T?> GetJsonAsync<T>(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(HttpMethod.Get, path, query);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken);
    }

    protected async Task SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string?>? query,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, path, query);
        request.Content = content;
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    protected StringContent CreateJsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, SerializerOptions), System.Text.Encoding.UTF8, "application/json");

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string?>? query)
    {
        if (!Config.IsConfigured)
        {
            throw new InvalidOperationException("Arr instance is not fully configured.");
        }

        var request = new HttpRequestMessage(method, BuildUri(path, query));
        request.Headers.TryAddWithoutValidation("X-Api-Key", Config.ApiKey);
        return request;
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var baseUri = new Uri(Config.BaseUrl.TrimEnd('/') + "/");
        var builder = new UriBuilder(new Uri(baseUri, "api/v3/" + path.TrimStart('/')));

        if (query is null || query.Count == 0)
        {
            return builder.Uri;
        }

        var parts = new List<string>();
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }

        builder.Query = string.Join("&", parts);
        return builder.Uri;
    }
}
