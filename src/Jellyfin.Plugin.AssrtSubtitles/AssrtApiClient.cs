using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssrtSubtitles.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Lightweight client for the assrt.net API.
/// </summary>
public class AssrtApiClient
{
    private const string BaseUrl = "https://api.assrt.net/v1";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssrtApiClient> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new ()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="AssrtApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Injected HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public AssrtApiClient(IHttpClientFactory httpClientFactory, ILogger<AssrtApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Performs a search against assrt.net.
    /// </summary>
    public async Task<IReadOnlyList<AssrtSubtitleEntry>> SearchAsync(string token, string query, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/sub/search?token={Uri.EscapeDataString(token)}&q={Uri.EscapeDataString(query)}&cnt=20&is_file=1&filelist=1";
        var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        return response?.Sub?.Subs ?? new List<AssrtSubtitleEntry>();
    }

    /// <summary>
    /// Fetch subtitle details including download links.
    /// </summary>
    public async Task<AssrtSubtitleEntry?> GetDetailAsync(string token, int subtitleId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/sub/detail?token={Uri.EscapeDataString(token)}&id={subtitleId.ToString(CultureInfo.InvariantCulture)}&filelist=1";
        var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        return response?.Sub?.Subs?.Count > 0 ? response.Sub.Subs[0] : null;
    }

    /// <summary>
    /// Downloads a file from assrt.net with appropriate headers and status checking.
    /// </summary>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("*/*"));
        request.Headers.Referrer = new Uri("https://assrt.net/");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? string.Empty;
            _logger.LogWarning("Download failed for {Url}: HTTP {StatusCode} {Reason}", url, statusCode, reason);
            throw new HttpRequestException($"Download failed with status {statusCode} ({reason})", null, response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssrtResponse?> SendAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<AssrtResponse>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Assrt API request to {Url} failed: HTTP {StatusCode}", url, ex.StatusCode);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse response from {Url}", url);
            return null;
        }
    }
}
