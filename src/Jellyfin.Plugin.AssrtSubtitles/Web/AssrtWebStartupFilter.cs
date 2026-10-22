using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssrtSubtitles.Web;

/// <summary>
/// Startup filter that registers middleware to serve the custom subtitle search UI script
/// and capture custom search keywords on Jellyfin's remote subtitle search endpoint.
/// </summary>
public class AssrtWebStartupFilter : IStartupFilter
{
    private const string ScriptResourceName = "Jellyfin.Plugin.AssrtSubtitles.Web.AssrtSubtitleEditorScript.js";
    private const string InjectedScriptTag = "<script defer=\"defer\" src=\"AssrtSubtitles/ClientScript.js?v=0.3.0\"></script>";

    private static readonly Regex SubtitleSearchRouteRegex = new(
        @"/Items/(?<itemId>[0-9a-fA-F\-]{32,36})/RemoteSearch/Subtitles/(?<lang>[^/?]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<AssrtWebStartupFilter> _logger;

    private string? _cachedScriptContent;
    private string? _cachedInjectedIndexHtml;
    private DateTime _cachedIndexLastWriteUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssrtWebStartupFilter"/> class.
    /// </summary>
    public AssrtWebStartupFilter(IApplicationPaths applicationPaths, ILogger<AssrtWebStartupFilter> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeMiddlewareAsync);
            next(app);
        };
    }

    private async Task InvokeMiddlewareAsync(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // 1. Serve ClientScript.js
        if (HttpMethods.IsGet(method) && path.EndsWith("/AssrtSubtitles/ClientScript.js", StringComparison.OrdinalIgnoreCase))
        {
            var script = GetClientScript();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/javascript; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-cache";
            await context.Response.WriteAsync(script, Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 2. Serve PreviewQueries endpoint for the Subtitle Editor UI
        if (HttpMethods.IsGet(method) && path.EndsWith("/AssrtSubtitles/PreviewQueries", StringComparison.OrdinalIgnoreCase))
        {
            var itemId = context.Request.Query["itemId"].ToString();
            var preview = AssrtSubtitleProvider.Instance?.GetPreviewQueries(itemId)
                          ?? new AssrtSubtitleProvider.PreviewQueriesResult { ItemId = itemId };

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            var json = JsonSerializer.Serialize(preview, CamelCaseJsonOptions);
            await context.Response.WriteAsync(json, Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 3. Optional POST /AssrtSubtitles/CustomQuery endpoint
        if (HttpMethods.IsPost(method) && path.EndsWith("/AssrtSubtitles/CustomQuery", StringComparison.OrdinalIgnoreCase))
        {
            var itemId = context.Request.Query["itemId"].ToString();
            var query = context.Request.Query["query"].ToString();
            var appendStr = context.Request.Query["appendEpisode"].ToString();
            var appendEpisode = !string.Equals(appendStr, "0", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(appendStr, "false", StringComparison.OrdinalIgnoreCase);

            AssrtSubtitleProvider.Instance?.SetCustomSearchOverride(itemId, query, appendEpisode);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"ok\":true}", Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // 4. Intercept GET /Items/{itemId}/RemoteSearch/Subtitles/{language}
        if (HttpMethods.IsGet(method) && path.Contains("/RemoteSearch/Subtitles/", StringComparison.OrdinalIgnoreCase))
        {
            var routeMatch = SubtitleSearchRouteRegex.Match(path);
            if (routeMatch.Success)
            {
                Guid? itemId = Guid.TryParse(routeMatch.Groups["itemId"].Value, out var parsedId) ? parsedId : null;
                var hasQueryParam = context.Request.Query.TryGetValue("assrtQuery", out var queryValues);
                var customQuery = hasQueryParam ? queryValues.ToString() : null;

                if (!hasQueryParam && context.Request.Headers.TryGetValue("X-Assrt-Query", out var headerValues))
                {
                    hasQueryParam = true;
                    customQuery = headerValues.ToString();
                }

                var appendEpisode = true;
                if (context.Request.Query.TryGetValue("assrtAppendEpisode", out var appendValues))
                {
                    var appendRaw = appendValues.ToString();
                    appendEpisode = !string.Equals(appendRaw, "0", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(appendRaw, "false", StringComparison.OrdinalIgnoreCase);
                }

                using var scope = AssrtSubtitleProvider.Instance?.BeginRequestSearchContext(itemId, customQuery, appendEpisode, hasQueryParam);
                await next().ConfigureAwait(false);
                return;
            }
        }

        // 5. Inject ClientScript.js into /web/index.html (or /web/) when custom UI is enabled
        if (HttpMethods.IsGet(method)
            && (Plugin.Instance?.Configuration.EnableCustomSearchUi ?? true)
            && IsWebIndexRequest(path))
        {
            var injectedHtml = TryGetInjectedIndexHtmlFromDisk();
            if (injectedHtml != null)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-cache";
                await context.Response.WriteAsync(injectedHtml, Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool IsWebIndexRequest(string path)
    {
        return string.Equals(path, "/web/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, "/web/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase);
    }

    private string? TryGetInjectedIndexHtmlFromDisk()
    {
        try
        {
            var webPath = _applicationPaths.WebPath;
            if (string.IsNullOrWhiteSpace(webPath))
            {
                return null;
            }

            var indexPath = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexPath))
            {
                return null;
            }

            var lastWriteUtc = File.GetLastWriteTimeUtc(indexPath);
            if (_cachedInjectedIndexHtml != null && lastWriteUtc == _cachedIndexLastWriteUtc)
            {
                return _cachedInjectedIndexHtml;
            }

            var rawHtml = File.ReadAllText(indexPath, Encoding.UTF8);
            if (!rawHtml.Contains("AssrtSubtitles/ClientScript.js", StringComparison.OrdinalIgnoreCase))
            {
                var bodyCloseIdx = rawHtml.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyCloseIdx >= 0)
                {
                    rawHtml = rawHtml.Insert(bodyCloseIdx, InjectedScriptTag);
                }
                else
                {
                    rawHtml += InjectedScriptTag;
                }
            }

            _cachedIndexLastWriteUtc = lastWriteUtc;
            _cachedInjectedIndexHtml = rawHtml;
            return _cachedInjectedIndexHtml;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read or inject index.html from WebPath");
            return null;
        }
    }

    private string GetClientScript()
    {
        if (_cachedScriptContent != null)
        {
            return _cachedScriptContent;
        }

        var assembly = typeof(AssrtWebStartupFilter).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(ScriptResourceName);
        if (stream == null)
        {
            return "// AssrtSubtitleEditorScript.js resource not found";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        _cachedScriptContent = reader.ReadToEnd();
        return _cachedScriptContent;
    }
}
