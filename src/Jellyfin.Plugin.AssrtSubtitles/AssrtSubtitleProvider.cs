using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssrtSubtitles.Configuration;
using Jellyfin.Plugin.AssrtSubtitles.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Subtitle provider for assrt.net.
/// </summary>
public class AssrtSubtitleProvider : ISubtitleProvider
{
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".ass", ".ssa", ".sub", ".vtt" };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".strm", ".iso", ".rmvb", ".flv" };

    private static readonly Dictionary<string, string> LanguageKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["langeng"] = "eng",
        ["langen"] = "eng",
        ["langzho"] = "zho",
        ["langchi"] = "zho",
        ["langchs"] = "zho",
        ["langcht"] = "zho",
        ["langdou"] = "zho",
        ["langkor"] = "kor",
        ["langjpn"] = "jpn",
        ["langjap"] = "jpn",
        ["langfre"] = "fra",
        ["langfra"] = "fra",
        ["langspa"] = "spa",
        ["langpor"] = "por",
        ["langpol"] = "pol",
        ["langdut"] = "nld",
        ["langger"] = "deu",
        ["langdeu"] = "deu",
        ["langita"] = "ita",
        ["langrus"] = "rus",
        ["langtha"] = "tha",
        ["langvie"] = "vie",
        ["langukr"] = "ukr"
    };

    private static readonly Dictionary<char, int> ChineseDigitMap = new()
    {
        ['零'] = 0, ['〇'] = 0,
        ['一'] = 1, ['壹'] = 1, ['Ⅰ'] = 1,
        ['二'] = 2, ['两'] = 2, ['贰'] = 2, ['Ⅱ'] = 2,
        ['三'] = 3, ['叁'] = 3, ['Ⅲ'] = 3,
        ['四'] = 4, ['肆'] = 4, ['Ⅳ'] = 4,
        ['五'] = 5, ['伍'] = 5, ['Ⅴ'] = 5,
        ['六'] = 6, ['陆'] = 6, ['Ⅵ'] = 6,
        ['七'] = 7, ['柒'] = 7, ['Ⅶ'] = 7,
        ['八'] = 8, ['捌'] = 8, ['Ⅷ'] = 8,
        ['九'] = 9, ['玖'] = 9, ['Ⅸ'] = 9,
        ['十'] = 10, ['拾'] = 10, ['Ⅹ'] = 10
    };

    private static readonly AsyncLocal<CustomSearchContext?> CurrentRequestSearchContext = new();

    private readonly AssrtApiClient _apiClient;
    private readonly ILogger<AssrtSubtitleProvider> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PluginLocalizer _localizer;
    private readonly ConcurrentDictionary<string, SearchContext> _contextCache = new();
    private readonly ConcurrentDictionary<string, CustomSearchContext> _customSearchOverrides = new(StringComparer.OrdinalIgnoreCase);
    private PluginConfiguration _configuration = new();
    private List<string> _preferredLanguages = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AssrtSubtitleProvider"/> class.
    /// </summary>
    public AssrtSubtitleProvider(
        AssrtApiClient apiClient,
        ILogger<AssrtSubtitleProvider> logger,
        IServiceProvider serviceProvider,
        PluginLocalizer? localizer = null)
    {
        _apiClient = apiClient;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _localizer = localizer ?? serviceProvider.GetService<PluginLocalizer>() ?? new PluginLocalizer();
        Instance = this;
        if (Plugin.Instance?.Configuration is { } config)
        {
            ConfigurationChanged(config);
        }
    }

    /// <summary>
    /// Gets the singleton provider instance.
    /// </summary>
    public static AssrtSubtitleProvider? Instance { get; private set; }

    /// <inheritdoc />
    public string Name => "Assrt.net";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode, VideoContentType.Movie };

    /// <summary>
    /// Sends user-facing message notifications to all connected client sessions.
    /// </summary>
    public async Task SendNotificationAsync(string header, string text, int timeoutMs = 4000)
    {
        try
        {
            var sessionManager = _serviceProvider.GetService<ISessionManager>();
            if (sessionManager == null)
            {
                return;
            }

            var sessions = sessionManager.Sessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .ToList();

            _logger.LogInformation("Broadcasting notification [{Header}]: {Text} (target sessions: {Count})", header, text, sessions.Count);

            var generalCmd = new GeneralCommand
            {
                Name = GeneralCommandType.DisplayMessage,
                Arguments =
                {
                    ["Header"] = header,
                    ["Text"] = text,
                    ["TimeoutMs"] = timeoutMs.ToString(CultureInfo.InvariantCulture)
                }
            };

            var msgCmd = new MessageCommand
            {
                Header = header,
                Text = text,
                TimeoutMs = timeoutMs
            };

            foreach (var session in sessions)
            {
                try
                {
                    await sessionManager.SendGeneralCommand(session.Id, null, generalCmd, CancellationToken.None).ConfigureAwait(false);
                    await sessionManager.SendMessageCommand(session.Id, string.Empty, msgCmd, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send notification to session {SessionId}", session.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast message command");
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = GetApiToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Assrt API token not configured; skipping search");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var queries = BuildQueries(request, out var isCustomQuery);
        if (queries.Count == 0)
        {
            _logger.LogDebug("Could not build a search query for the incoming request");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var preferredLanguages = BuildPreferredLanguageList(request);
        var contextKey = StoreSearchContext(request, preferredLanguages);

        var mergedEntries = new List<(AssrtSubtitleEntry Entry, string MatchedQuery)>();
        var seenIds = new HashSet<int>();
        var maxQueriesToRun = isCustomQuery ? Math.Min(queries.Count, 4) : Math.Min(queries.Count, 5);

        for (var i = 0; i < maxQueriesToRun; i++)
        {
            var query = queries[i];
            _logger.LogInformation("Searching Assrt [{Index}/{Total}] (custom={IsCustom}): {Query}", i + 1, maxQueriesToRun, isCustomQuery, query);
            var results = await _apiClient.SearchAsync(token, query, cancellationToken).ConfigureAwait(false);
            foreach (var entry in results)
            {
                if (seenIds.Add(entry.Id))
                {
                    mergedEntries.Add((entry, query));
                }
            }

            if (mergedEntries.Count >= 18)
            {
                break;
            }
        }

        return mergedEntries.Select(item => MapToResult(item.Entry, preferredLanguages, request.Language, contextKey, item.MatchedQuery));
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (subtitleId, context) = ParseSubtitleId(id);
        var header = _localizer.NotificationHeader;

        var token = GetApiToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            var tokenErr = _localizer.ErrTokenNotConfigured;
            await SendNotificationAsync(header, "❌ " + tokenErr, 6000).ConfigureAwait(false);
            throw new InvalidOperationException(tokenErr);
        }

        await SendNotificationAsync(header, _localizer.MsgFetchingSubtitleDetail(subtitleId), 2500).ConfigureAwait(false);

        var detail = await _apiClient.GetDetailAsync(token, subtitleId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            var notFoundErr = _localizer.ErrSubtitleDetailNotFound(subtitleId);
            await SendNotificationAsync(header, "❌ " + notFoundErr, 5000).ConfigureAwait(false);
            throw new FileNotFoundException(notFoundErr);
        }

        var preferredLanguages = context?.PreferredLanguages is { Count: > 0 } ctxLangs ? ctxLangs : _preferredLanguages;
        var language = ResolveLanguage(detail.LanguageInfo, preferredLanguages, context?.RequestLanguage);
        var title = detail.NativeName ?? detail.VideoName ?? detail.Title ?? $"Assrt #{subtitleId}";

        var candidates = GetDownloadCandidates(detail, preferredLanguages, context);
        if (candidates.Count == 0)
        {
            var noFileErr = _localizer.ErrNoDownloadableFiles(title);
            await SendNotificationAsync(header, "❌ " + noFileErr, 5000).ConfigureAwait(false);
            throw new FileNotFoundException(noFileErr);
        }

        await SendNotificationAsync(header, _localizer.MsgDownloadingSubtitle(title), 3000).ConfigureAwait(false);

        byte[]? downloadedData = null;
        AssrtFileEntry? successfulFile = null;
        Exception? lastException = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            try
            {
                if (i > 0)
                {
                    await SendNotificationAsync(header, _localizer.MsgSwitchingFallbackCandidate(i + 1, candidates.Count), 2500).ConfigureAwait(false);
                }

                _logger.LogInformation("Downloading from Assrt [{Index}/{Total}]: {FileName} ({Url})", i + 1, candidates.Count, candidate.FileName, candidate.Url);
                downloadedData = await _apiClient.DownloadAsync(candidate.Url!, cancellationToken).ConfigureAwait(false);
                if (downloadedData != null && downloadedData.Length > 0)
                {
                    successfulFile = candidate;
                    break;
                }
            }
            catch (HttpRequestException ex) when ((int?)ex.StatusCode == 493)
            {
                lastException = ex;
                _logger.LogWarning("Download candidate {FileName} returned HTTP 493, trying next candidate...", candidate.FileName);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "Download candidate {FileName} failed: {Message}, trying next candidate...", candidate.FileName, ex.Message);
            }
        }

        if (downloadedData == null || successfulFile == null)
        {
            var errMessage = lastException is HttpRequestException { StatusCode: (System.Net.HttpStatusCode)493 }
                ? _localizer.ErrDownloadHttp493
                : _localizer.ErrDownloadGeneral(lastException?.Message);

            await SendNotificationAsync(header, "❌ " + errMessage, 7000).ConfigureAwait(false);
            throw new InvalidOperationException(errMessage, lastException);
        }

        var extension = GetExtension(successfulFile.FileName);
        if (ArchiveExtensions.Contains(extension) || IsArchiveData(downloadedData))
        {
            await SendNotificationAsync(header, _localizer.MsgExtractingAndMatching, 2500).ConfigureAwait(false);
            var (stream, format, selectedName, batchSavedCount) = ExtractAndAutoDeployFromArchive(
                downloadedData,
                preferredLanguages,
                language ?? "zho",
                context);

            if (stream is null)
            {
                var extractErr = _localizer.ErrExtractFailed(successfulFile.FileName ?? string.Empty);
                await SendNotificationAsync(header, "❌ " + extractErr, 6000).ConfigureAwait(false);
                throw new InvalidDataException(extractErr);
            }

            var fmt = format ?? "srt";
            var epInfo = context?.EpisodeNumber.HasValue == true
                ? _localizer.MsgMatchedFileSuffix(Path.GetFileName(selectedName))
                : string.Empty;

            if (batchSavedCount > 0)
            {
                await SendNotificationAsync(
                    header,
                    _localizer.MsgSuccessWithBatchUnpack(fmt.ToUpperInvariant(), epInfo, batchSavedCount),
                    8000).ConfigureAwait(false);
            }
            else
            {
                await SendNotificationAsync(
                    header,
                    _localizer.MsgSuccessSingle(fmt.ToUpperInvariant(), epInfo),
                    6000).ConfigureAwait(false);
            }

            return new SubtitleResponse
            {
                Format = fmt,
                Language = language,
                Stream = stream
            };
        }

        var directFormat = NormalizeFormatFromExtension(extension);
        await SendNotificationAsync(header, _localizer.MsgSuccessSingle(directFormat.ToUpperInvariant()), 6000).ConfigureAwait(false);

        return new SubtitleResponse
        {
            Format = directFormat,
            Language = language,
            Stream = new MemoryStream(downloadedData)
        };
    }

    /// <summary>
    /// Updates internal configuration cache.
    /// </summary>
    public void ConfigurationChanged(PluginConfiguration configuration)
    {
        _configuration = configuration;
        _preferredLanguages = configuration.PreferredLanguages
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .ToList();
    }

    private string? GetApiToken()
    {
        var token = _configuration.ApiToken?.Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private string StoreSearchContext(SubtitleSearchRequest request, IReadOnlyList<string> preferredLanguages)
    {
        // Prune expired cache entries (> 6 hours)
        var cutoff = DateTime.UtcNow.AddHours(-6);
        foreach (var kv in _contextCache)
        {
            if (kv.Value.CreatedAt < cutoff)
            {
                _contextCache.TryRemove(kv.Key, out _);
            }
        }

        int? season = request.ParentIndexNumber;
        int? episode = request.IndexNumber;

        // Fallback to parsing MediaPath if season/episode wasn't populated in request
        if ((!season.HasValue || !episode.HasValue) && !string.IsNullOrWhiteSpace(request.MediaPath))
        {
            var (parsedSeason, parsedEpisode) = TryParseSeasonEpisode(Path.GetFileNameWithoutExtension(request.MediaPath));
            season ??= parsedSeason;
            episode ??= parsedEpisode;
        }

        var key = Guid.NewGuid().ToString("N")[..10];
        _contextCache[key] = new SearchContext
        {
            ContentType = request.ContentType,
            SeriesName = request.SeriesName,
            SeasonNumber = season,
            EpisodeNumber = episode,
            MediaPath = request.MediaPath,
            RequestLanguage = request.Language,
            PreferredLanguages = preferredLanguages.ToList(),
            CreatedAt = DateTime.UtcNow
        };

        return key;
    }

    private (int SubtitleId, SearchContext? Context) ParseSubtitleId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException(_localizer.ErrSubtitleIdEmpty, nameof(id));
        }

        var parts = id.Split(':', 2);
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var subtitleId))
        {
            throw new ArgumentException(_localizer.ErrSubtitleIdNotNumeric, nameof(id));
        }

        SearchContext? context = null;
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            _contextCache.TryGetValue(parts[1], out context);
        }

        return (subtitleId, context);
    }

    /// <summary>
    /// Sets the custom search context for the current async HTTP request pipeline and short-lived fallback cache.
    /// </summary>
    public IDisposable BeginRequestSearchContext(Guid? itemId, string? customQuery, bool appendEpisode, bool hasQueryParam)
    {
        var ctx = new CustomSearchContext
        {
            ItemId = itemId,
            CustomQuery = string.IsNullOrWhiteSpace(customQuery) ? null : customQuery.Trim(),
            AppendEpisode = appendEpisode,
            CreatedAt = DateTime.UtcNow
        };

        CurrentRequestSearchContext.Value = ctx;

        if (hasQueryParam)
        {
            SetCustomSearchOverride(itemId?.ToString("N"), customQuery, appendEpisode);
        }

        return new ActionDisposable(() => CurrentRequestSearchContext.Value = null);
    }

    /// <summary>
    /// Stores or clears a short-lived custom search query override.
    /// </summary>
    public void SetCustomSearchOverride(string? itemId, string? customQuery, bool appendEpisode)
    {
        PruneExpiredCustomSearchOverrides();

        var normalizedKey = NormalizeItemIdKey(itemId);
        if (string.IsNullOrWhiteSpace(customQuery))
        {
            if (!string.IsNullOrEmpty(normalizedKey))
            {
                _customSearchOverrides.TryRemove(normalizedKey, out _);
            }

            _customSearchOverrides.TryRemove("__latest__", out _);
            return;
        }

        Guid? parsedGuid = Guid.TryParse(itemId, out var g) ? g : null;
        var entry = new CustomSearchContext
        {
            ItemId = parsedGuid,
            CustomQuery = customQuery.Trim(),
            AppendEpisode = appendEpisode,
            CreatedAt = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(normalizedKey))
        {
            _customSearchOverrides[normalizedKey] = entry;
        }

        _customSearchOverrides["__latest__"] = entry;
    }

    /// <summary>
    /// Returns preview information and auto-generated candidate queries for the specified media item ID.
    /// </summary>
    public PreviewQueriesResult GetPreviewQueries(string? itemIdString)
    {
        var result = new PreviewQueriesResult
        {
            EnableCustomUi = _configuration.EnableCustomSearchUi,
            ItemId = itemIdString ?? string.Empty
        };

        if (!Guid.TryParse(itemIdString, out var itemId))
        {
            return result;
        }

        var libraryManager = _serviceProvider.GetService<ILibraryManager>();
        var item = libraryManager?.GetItemById(itemId);
        if (item == null)
        {
            return result;
        }

        var request = new SubtitleSearchRequest
        {
            MediaPath = item.Path,
            Name = item.Name,
            ProductionYear = item.ProductionYear
        };

        if (item is Episode ep)
        {
            request.ContentType = VideoContentType.Episode;
            request.SeriesName = ep.SeriesName ?? ep.Series?.Name;
            request.ParentIndexNumber = ep.ParentIndexNumber;
            request.IndexNumber = ep.IndexNumber;
            result.IsEpisode = true;
            result.SeriesName = request.SeriesName;
            result.OriginalTitle = ep.Series?.OriginalTitle ?? ep.OriginalTitle;
            result.SeasonNumber = ep.ParentIndexNumber;
            result.EpisodeNumber = ep.IndexNumber;
        }
        else
        {
            request.ContentType = VideoContentType.Movie;
            result.IsEpisode = false;
            result.OriginalTitle = item.OriginalTitle;
        }

        result.Name = item.Name;
        var meta = ResolveMediaMetadata(request, itemId);
        result.SeasonNumber ??= meta.SeasonNumber;
        result.EpisodeNumber ??= meta.EpisodeNumber;
        result.TagKeywords = meta.TagKeywords;
        result.Queries = BuildAutomaticQueries(request, meta);
        return result;
    }

    private void PruneExpiredCustomSearchOverrides()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (var kv in _customSearchOverrides)
        {
            if (kv.Value.CreatedAt < cutoff)
            {
                _customSearchOverrides.TryRemove(kv.Key, out _);
            }
        }
    }

    private static string NormalizeItemIdKey(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        return Guid.TryParse(itemId, out var g) ? g.ToString("N") : itemId.Trim();
    }

    private CustomSearchContext? ResolveActiveCustomSearch(Guid? resolvedItemId)
    {
        if (CurrentRequestSearchContext.Value is { } asyncCtx && !string.IsNullOrWhiteSpace(asyncCtx.CustomQuery))
        {
            return asyncCtx;
        }

        PruneExpiredCustomSearchOverrides();

        if (resolvedItemId.HasValue && _customSearchOverrides.TryRemove(resolvedItemId.Value.ToString("N"), out var byItem))
        {
            _customSearchOverrides.TryRemove("__latest__", out _);
            return byItem;
        }

        if (_customSearchOverrides.TryRemove("__latest__", out var latest))
        {
            return latest;
        }

        return null;
    }

    private List<string> BuildQueries(SubtitleSearchRequest request, out bool isCustomQuery)
    {
        var requestItemId = CurrentRequestSearchContext.Value?.ItemId;
        var meta = ResolveMediaMetadata(request, requestItemId);
        var activeCustom = ResolveActiveCustomSearch(meta.ItemId);

        if (activeCustom != null && !string.IsNullOrWhiteSpace(activeCustom.CustomQuery))
        {
            isCustomQuery = true;
            return BuildCustomOverrideQueries(request, meta, activeCustom.CustomQuery, activeCustom.AppendEpisode);
        }

        isCustomQuery = false;
        return BuildAutomaticQueries(request, meta);
    }

    private static List<string> BuildCustomOverrideQueries(
        SubtitleSearchRequest request,
        MediaMetadataInfo meta,
        string customQueryInput,
        bool appendEpisode)
    {
        var queries = new List<string>();
        var segments = customQueryInput
            .Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var isEpisode = request.ContentType == VideoContentType.Episode || meta.EpisodeNumber.HasValue;
        var season = meta.SeasonNumber;
        var episode = meta.EpisodeNumber;

        foreach (var kw in segments)
        {
            if (string.IsNullOrWhiteSpace(kw))
            {
                continue;
            }

            if (isEpisode && appendEpisode && !HasExplicitSeasonOrEpisode(kw))
            {
                if (season.HasValue && episode.HasValue)
                {
                    queries.Add($"{kw} S{season.Value:00}E{episode.Value:00}");
                    queries.Add($"{kw} S{season.Value:00}");
                    queries.Add(kw);
                }
                else if (episode.HasValue)
                {
                    queries.Add($"{kw} E{episode.Value:00}");
                    queries.Add(kw);
                }
                else
                {
                    queries.Add(kw);
                }
            }
            else
            {
                queries.Add(kw);
            }
        }

        return queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> BuildAutomaticQueries(SubtitleSearchRequest request, MediaMetadataInfo meta)
    {
        var queries = new List<string>();
        var season = meta.SeasonNumber;
        var episode = meta.EpisodeNumber;

        if (request.ContentType == VideoContentType.Episode)
        {
            var seriesCandidates = new List<string>();

            // 1. Metadata tags (e.g. assrt:Stranger Things) & config alias mappings
            seriesCandidates.AddRange(meta.TagKeywords);
            seriesCandidates.AddRange(meta.ConfiguredAliases);

            // 2. Cleaned base SeriesName (strips trailing "Season 1", "第一季", "(2024)", etc.)
            AddCandidateTitle(seriesCandidates, CleanSeriesTitle(request.SeriesName));

            // 3. Series OriginalTitle from Jellyfin Library (e.g. English/Japanese original title)
            AddCandidateTitle(seriesCandidates, CleanSeriesTitle(meta.SeriesOriginalTitle));

            // 4. Titles extracted from video filename and parent series folder
            foreach (var pathTitle in ExtractSeriesTitlesFromPath(request.MediaPath))
            {
                AddCandidateTitle(seriesCandidates, pathTitle);
            }

            // 5. Fallback to raw SeriesName if cleaning removed everything
            if (seriesCandidates.Count == 0)
            {
                AddCandidateTitle(seriesCandidates, request.SeriesName);
            }

            seriesCandidates = seriesCandidates
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Tier 1: Exact Episode queries (e.g. "SeriesName S01E01", "OriginalTitle S01E01")
            if (episode.HasValue)
            {
                foreach (var title in seriesCandidates)
                {
                    if (HasExplicitSeasonOrEpisode(title))
                    {
                        queries.Add(title);
                    }
                    else if (season.HasValue)
                    {
                        queries.Add($"{title} S{season.Value:00}E{episode.Value:00}");
                    }
                    else
                    {
                        queries.Add($"{title} E{episode.Value:00}");
                    }
                }
            }

            // Tier 2: Season pack queries (e.g. "SeriesName S01", "OriginalTitle S01", "SeriesName 第一季")
            if (season.HasValue)
            {
                foreach (var title in seriesCandidates.Take(3))
                {
                    if (!HasExplicitSeasonOrEpisode(title))
                    {
                        queries.Add($"{title} S{season.Value:00}");
                        if (ContainsCjk(title) && season.Value is >= 1 and <= 20)
                        {
                            queries.Add($"{title} 第{ToChineseNumber(season.Value)}季");
                        }
                    }
                }
            }

            // Tier 3: Bare series title fallback (for full-series packs or miniseries)
            foreach (var title in seriesCandidates.Take(2))
            {
                queries.Add(title);
            }
        }
        else
        {
            // Movie / non-episode query generation
            var year = meta.ProductionYear;

            // 1. Metadata tags & configured aliases
            foreach (var tagKw in meta.TagKeywords.Concat(meta.ConfiguredAliases))
            {
                queries.Add(tagKw);
            }

            // 2. Movie Name in Jellyfin
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                queries.Add(request.Name.Trim());
                var cleanedName = CleanMovieTitle(request.Name);
                if (!string.IsNullOrWhiteSpace(cleanedName))
                {
                    queries.Add(cleanedName);
                }
            }

            // 3. OriginalTitle from Jellyfin Library (e.g. "Dune: Part Two")
            if (!string.IsNullOrWhiteSpace(meta.OriginalTitle))
            {
                var cleanOrig = CleanMovieTitle(meta.OriginalTitle) ?? meta.OriginalTitle.Trim();
                if (year.HasValue && !cleanOrig.Contains(year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    queries.Add($"{cleanOrig} {year.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                queries.Add(cleanOrig);
            }

            // 4. Cleaned titles extracted from filename / parent folder
            foreach (var pathQuery in ExtractMovieTitlesFromPath(request.MediaPath, year))
            {
                queries.Add(pathQuery);
            }

            // 5. Movie Name + Year fallback
            if (!string.IsNullOrWhiteSpace(request.Name) && year.HasValue &&
                !request.Name.Contains(year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                queries.Add($"{request.Name.Trim()} {year.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (queries.Count == 0 && !string.IsNullOrWhiteSpace(request.MediaPath))
        {
            queries.Add(Path.GetFileNameWithoutExtension(request.MediaPath));
        }

        return queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => Regex.Replace(q.Trim(), @"\s{2,}", " "))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private MediaMetadataInfo ResolveMediaMetadata(SubtitleSearchRequest request, Guid? knownItemId)
    {
        var info = new MediaMetadataInfo
        {
            ItemId = knownItemId,
            SeasonNumber = request.ParentIndexNumber,
            EpisodeNumber = request.IndexNumber,
            ProductionYear = request.ProductionYear
        };

        if ((!info.SeasonNumber.HasValue || !info.EpisodeNumber.HasValue) && !string.IsNullOrWhiteSpace(request.MediaPath))
        {
            var (parsedSeason, parsedEpisode) = TryParseSeasonEpisode(request.MediaPath);
            info.SeasonNumber ??= parsedSeason;
            info.EpisodeNumber ??= parsedEpisode;
        }

        try
        {
            var libraryManager = _serviceProvider.GetService<ILibraryManager>();
            if (libraryManager != null)
            {
                BaseItem? item = null;
                if (knownItemId.HasValue && knownItemId.Value != Guid.Empty)
                {
                    item = libraryManager.GetItemById(knownItemId.Value);
                }

                if (item == null && !string.IsNullOrWhiteSpace(request.MediaPath))
                {
                    item = libraryManager.FindByPath(request.MediaPath, isFolder: false);
                }

                if (item != null)
                {
                    info.ItemId = item.Id;
                    info.ProductionYear ??= item.ProductionYear;
                    ExtractCustomTags(item.Tags, info.TagKeywords);

                    if (item is Episode ep)
                    {
                        info.SeasonNumber ??= ep.ParentIndexNumber;
                        info.EpisodeNumber ??= ep.IndexNumber;

                        var series = ep.Series;
                        var season = ep.Season;
                        if (season != null)
                        {
                            ExtractCustomTags(season.Tags, info.TagKeywords);
                        }

                        if (series != null)
                        {
                            info.SeriesOriginalTitle = series.OriginalTitle;
                            info.ProductionYear ??= series.ProductionYear;
                            ExtractCustomTags(series.Tags, info.TagKeywords);
                        }

                        info.OriginalTitle = ep.OriginalTitle;
                    }
                    else
                    {
                        info.OriginalTitle = item.OriginalTitle;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve extra library metadata for subtitle search");
        }

        // Resolve configured keyword alias mappings
        ResolveConfiguredAliases(request, info);

        return info;
    }

    private static void ExtractCustomTags(string[]? tags, List<string> target)
    {
        if (tags == null || tags.Length == 0)
        {
            return;
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var match = Regex.Match(tag.Trim(), @"^(?:assrt|sub|subtitle|字幕)\s*[:=：]\s*(?<kw>.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var parts = match.Groups["kw"].Value.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    if (!string.IsNullOrWhiteSpace(p) && !target.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        target.Add(p);
                    }
                }
            }
        }
    }

    private void ResolveConfiguredAliases(SubtitleSearchRequest request, MediaMetadataInfo info)
    {
        var rawMappings = _configuration.CustomKeywordMappings;
        if (string.IsNullOrWhiteSpace(rawMappings))
        {
            return;
        }

        var candidateSources = new[]
        {
            request.SeriesName,
            request.Name,
            info.SeriesOriginalTitle,
            info.OriginalTitle,
            !string.IsNullOrWhiteSpace(request.MediaPath) ? Path.GetFileNameWithoutExtension(request.MediaPath) : null
        }
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s!.Trim())
        .ToList();

        if (candidateSources.Count == 0)
        {
            return;
        }

        var lines = rawMappings.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var sepIndex = line.IndexOf('=');
            if (sepIndex <= 0 || sepIndex >= line.Length - 1)
            {
                continue;
            }

            var sourcePattern = line[..sepIndex].Trim();
            var targetKeywords = line[(sepIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(sourcePattern) || string.IsNullOrWhiteSpace(targetKeywords))
            {
                continue;
            }

            var isMatch = candidateSources.Any(src =>
                string.Equals(src, sourcePattern, StringComparison.OrdinalIgnoreCase) ||
                src.Contains(sourcePattern, StringComparison.OrdinalIgnoreCase));

            if (isMatch)
            {
                foreach (var kw in targetKeywords.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!info.ConfiguredAliases.Contains(kw, StringComparer.OrdinalIgnoreCase))
                    {
                        info.ConfiguredAliases.Add(kw);
                    }
                }
            }
        }
    }

    private static void AddCandidateTitle(List<string> list, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var trimmed = title.Trim();
        if (trimmed.Length >= 1 && !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(trimmed);
        }
    }

    private static string? CleanSeriesTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cleaned = title.Trim();
        // Strip trailing (2024) or （2024）
        cleaned = Regex.Replace(cleaned, @"\s*[\(\[（【]\d{4}[\)\]）】]\s*$", string.Empty);
        // Strip trailing "Season 1" / "S01" / "第一季"
        cleaned = Regex.Replace(cleaned, @"\s+(?:Season\s*\d{1,2}|S\d{1,2}|第[0-9零〇一二两三四五六七八九十百]+季)\s*$", string.Empty, RegexOptions.IgnoreCase);
        // Normalize colons/punctuation
        var noPunct = cleaned.Replace('：', ' ').Replace(':', ' ').Replace('-', ' ');
        noPunct = Regex.Replace(noPunct, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(noPunct) ? null : noPunct;
    }

    private static string? CleanMovieTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cleaned = Regex.Replace(title.Trim(), @"\s*[\(\[（【]\d{4}[\)\]）】]\s*$", string.Empty);
        cleaned = cleaned.Replace('：', ' ').Replace(':', ' ').Replace('·', ' ');
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static List<string> ExtractSeriesTitlesFromPath(string? mediaPath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return results;
        }

        var fileName = Path.GetFileNameWithoutExtension(mediaPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            // Match title before S01E01 / S01 / EP01 / 1x01
            var match = Regex.Match(
                fileName,
                @"^(?:\[[^\]]+\]\s*)*(?<title>.+?)(?:[\.\s\-_]+[\(\[]?\d{4}[\)\]]?)?[\.\s\-_]+(?:S\d{1,2}[\.\s\-_]*E\d{1,3}|S\d{1,2}(?![0-9a-zA-Z])|(?:EP|Episode)[\.\s\-_]*\d{1,3}|\d{1,2}x\d{2,3}|第\s*[0-9零〇一二两三四五六七八九十]+\s*[集话話季])",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                AddExtractedPathTitles(results, match.Groups["title"].Value);
            }
        }

        // Also check parent directories (e.g. /TV/Stranger.Things.S01/S01E01.mkv or /TV/狼厅.Wolf.Hall/Season 1/S01E01.mkv)
        var dir = Path.GetDirectoryName(mediaPath);
        for (var depth = 0; depth < 2 && !string.IsNullOrWhiteSpace(dir); depth++)
        {
            var folderName = Path.GetFileName(dir);
            dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            // Skip pure season folders like "Season 1", "Season 01", "S01", "Specials", "第1季"
            if (Regex.IsMatch(folderName.Trim(), @"^(?:Season\s*\d{1,2}|S\d{1,2}|Specials|SPs?|第[0-9零〇一二两三四五六七八九十]+季|BD|DVD|1080[pP]|2160[pP]|4[kK])$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            // Skip top-level library root folder names
            if (Regex.IsMatch(folderName.Trim(), @"^(?:TV|TV\s*Shows?|Series|Anime|Movies?|Media|Video|剧集|电视剧|动漫|电影|影视)$", RegexOptions.IgnoreCase))
            {
                break;
            }

            AddExtractedPathTitles(results, folderName);
            break;
        }

        return results;
    }

    private static List<string> ExtractMovieTitlesFromPath(string? mediaPath, int? knownYear)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return results;
        }

        var fileName = Path.GetFileNameWithoutExtension(mediaPath);
        ExtractCleanedMovieCandidates(fileName, knownYear, results);

        var parentDir = Path.GetDirectoryName(mediaPath);
        var folderName = !string.IsNullOrWhiteSpace(parentDir) ? Path.GetFileName(parentDir) : null;
        if (!string.IsNullOrWhiteSpace(folderName) &&
            !Regex.IsMatch(folderName.Trim(), @"^(?:Movies?|Media|Video|Film|电影|影视|BD|UHD|Remux)$", RegexOptions.IgnoreCase))
        {
            ExtractCleanedMovieCandidates(folderName, knownYear, results);
        }

        return results;
    }

    private static void ExtractCleanedMovieCandidates(string? rawName, int? knownYear, List<string> results)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return;
        }

        // Remove leading release group tags like [阳光电影] or 【xx】
        var cleaned = Regex.Replace(rawName, @"^(?:\[[^\]]+\]|【[^】]+】|\([^\)]+\))[\.\s\-_]*", string.Empty);

        // Cut off at year (19xx or 20xx) or resolution/source token
        var yearMatch = Regex.Match(cleaned, @"^(?<title>.+?)[\.\s\-_\(\[]+(?<year>(?:19|20)\d{2})(?![0-9pP])");
        if (yearMatch.Success)
        {
            var titlePart = yearMatch.Groups["title"].Value;
            var yearStr = yearMatch.Groups["year"].Value;
            var extracted = SplitBilingualAndCleanTitle(titlePart);
            foreach (var t in extracted)
            {
                if (!ContainsCjk(t))
                {
                    AddCandidateTitle(results, $"{t} {yearStr}");
                }

                AddCandidateTitle(results, t);
            }

            return;
        }

        // Otherwise strip common scene/release tags (1080p, 2160p, BluRay, WEB-DL, x264, etc.)
        var tokenMatch = Regex.Match(
            cleaned,
            @"^(?<title>.+?)[\.\s\-_]+(?:2160p|1080p|720p|480p|4k|uhd|bluray|bdrip|brrip|web-dl|webrip|web|hdtv|remux|dvdrip|x264|x265|h264|h265|hevc|avc|aac|ddp|dts|truehd|atmos|hdr|dv|10bit|chs|cht|中英|双语|国粤|中字)",
            RegexOptions.IgnoreCase);
        var basePart = tokenMatch.Success ? tokenMatch.Groups["title"].Value : cleaned;

        foreach (var t in SplitBilingualAndCleanTitle(basePart))
        {
            if (knownYear.HasValue && !ContainsCjk(t))
            {
                AddCandidateTitle(results, $"{t} {knownYear.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            AddCandidateTitle(results, t);
        }
    }

    private static void AddExtractedPathTitles(List<string> results, string rawSegment)
    {
        foreach (var t in SplitBilingualAndCleanTitle(rawSegment))
        {
            AddCandidateTitle(results, t);
        }
    }

    private static List<string> SplitBilingualAndCleanTitle(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return list;
        }

        // Remove bracketed tags and trailing year/season/scene markers
        var s = Regex.Replace(raw, @"^(?:\[[^\]]*\]|【[^】]*】)\s*", string.Empty);
        s = Regex.Replace(
            s,
            @"[\.\s\-_]+(?:S\d{1,2}(?:E\d{1,3})?|Season[\.\s\-_]*\d{1,2}|第[0-9零〇一二两三四五六七八九十]+季|2160p|1080p|720p|480p|4k|uhd|bluray|bdrip|brrip|web-dl|webrip|hdtv|remux|dvdrip|x264|x265|h264|h265|hevc).*$",
            string.Empty,
            RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[[^\]]*\]|【[^】]*】|\((?:19|20)\d{2}\)|（(?:19|20)\d{2}）", " ");
        s = s.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '·');
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            return list;
        }

        // Check if string starts with CJK title followed by English title (e.g., "狼厅 Wolf Hall" or "沙丘2 Dune Part Two")
        var bilingualMatch = Regex.Match(s, @"^(?<cjk>[\u4e00-\u9fff][\u4e00-\u9fff0-9\s：:·]{0,25}?)\s+(?<eng>[A-Za-z][A-Za-z0-9\s'\-:]{2,})$");
        if (bilingualMatch.Success)
        {
            var cjk = bilingualMatch.Groups["cjk"].Value.Trim();
            var eng = bilingualMatch.Groups["eng"].Value.Trim();
            if (cjk.Length >= 1)
            {
                list.Add(cjk);
            }

            if (eng.Length >= 2)
            {
                list.Add(eng);
            }

            return list;
        }

        if (s.Length >= 2 || ContainsCjk(s))
        {
            list.Add(s);
        }

        return list;
    }

    private static bool ContainsCjk(string text)
    {
        return Regex.IsMatch(text, @"[\u4e00-\u9fff]");
    }

    private static bool HasExplicitSeasonOrEpisode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var (season, episode) = TryParseSeasonEpisode(text);
        return season.HasValue || episode.HasValue;
    }

    private static string ToChineseNumber(int number)
    {
        string[] digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
        if (number is >= 1 and <= 10)
        {
            return digits[number];
        }

        if (number is > 10 and < 20)
        {
            return "十" + digits[number - 10];
        }

        if (number == 20)
        {
            return "二十";
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }

    private RemoteSubtitleInfo MapToResult(
        AssrtSubtitleEntry entry,
        IReadOnlyList<string> preferredLanguages,
        string? requestLanguage,
        string contextKey,
        string? matchedQuery = null)
    {
        var language = ResolveLanguage(entry.LanguageInfo, preferredLanguages, requestLanguage);
        var baseName = entry.NativeName ?? entry.VideoName ?? entry.Title ?? $"Assrt #{entry.Id}";
        var format = NormalizeFormat(entry.SubType);

        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(format))
        {
            tags.Add(format.ToUpperInvariant());
        }

        var langDesc = GetLanguageTag(entry.LanguageInfo);
        if (!string.IsNullOrWhiteSpace(langDesc))
        {
            tags.Add(langDesc);
        }

        if (entry.FileList is { Count: > 1 })
        {
            var distinctEpisodes = entry.FileList
                .Select(f => TryParseSeasonEpisode(f.FileName).Episode)
                .Where(e => e.HasValue)
                .Distinct()
                .Count();
            if (distinctEpisodes > 1)
            {
                tags.Add(_localizer.TagSeasonPack(distinctEpisodes));
            }
        }

        if (entry.VoteScore.HasValue && entry.VoteScore > 0)
        {
            tags.Add($"★{entry.VoteScore.Value:0.#}");
        }

        if (entry.DownCount.HasValue && entry.DownCount > 0)
        {
            tags.Add($"⬇{FormatCount(entry.DownCount.Value)}");
        }

        var displayName = tags.Count > 0 ? $"[{string.Join(" | ", tags)}] {baseName}" : baseName;

        var commentParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.ReleaseSite))
        {
            commentParts.Add(_localizer.CommentSource(entry.ReleaseSite));
        }

        if (!string.IsNullOrWhiteSpace(entry.LanguageInfo?.Description))
        {
            commentParts.Add(entry.LanguageInfo.Description);
        }

        if (!string.IsNullOrWhiteSpace(entry.FileName))
        {
            commentParts.Add(_localizer.CommentFile(entry.FileName));
        }

        if (!string.IsNullOrWhiteSpace(matchedQuery))
        {
            commentParts.Add(_localizer.CommentMatchedQuery(matchedQuery));
        }

        var comment = commentParts.Count > 0 ? string.Join(" | ", commentParts) : null;

        return new RemoteSubtitleInfo
        {
            Id = $"{entry.Id.ToString(CultureInfo.InvariantCulture)}:{contextKey}",
            ProviderName = Name,
            Name = displayName,
            Comment = comment,
            Author = entry.ReleaseSite,
            ThreeLetterISOLanguageName = language ?? requestLanguage,
            DateCreated = ParseDate(entry.UploadTime),
            CommunityRating = entry.VoteScore.HasValue ? (float?)entry.VoteScore.Value : null,
            DownloadCount = entry.DownCount,
            Format = format,
            IsHashMatch = false,
            HearingImpaired = false,
            MachineTranslated = false,
            AiTranslated = false,
            Forced = false
        };
    }

    private string? GetLanguageTag(AssrtLanguageInfo? info)
    {
        if (info == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            var d = info.Description;
            if (d.Contains("双语"))
            {
                return _localizer.TagBilingual;
            }

            if (d.Contains("简体") || d.Contains("简中") || d.Contains("chs", StringComparison.OrdinalIgnoreCase))
            {
                return _localizer.TagSimplifiedChinese;
            }

            if (d.Contains("繁体") || d.Contains("繁中") || d.Contains("cht", StringComparison.OrdinalIgnoreCase))
            {
                return _localizer.TagTraditionalChinese;
            }

            if (d.Contains("中英"))
            {
                return _localizer.TagChsEng;
            }
        }

        return null;
    }

    private static string FormatCount(int count)
    {
        if (count >= 1000)
        {
            return $"{count / 1000.0:0.#}k";
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsArchiveData(byte[] data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        // Zip: PK\x03\x04 or PK\x05\x06
        if (data[0] == 0x50 && data[1] == 0x4B)
        {
            return true;
        }

        // Rar: Rar!\x1A\x07
        if (data[0] == 0x52 && data[1] == 0x61 && data[2] == 0x72 && data[3] == 0x21)
        {
            return true;
        }

        // 7z: 7z\xBC\xAF\x27\x1C
        if (data.Length >= 6 && data[0] == 0x37 && data[1] == 0x7A && data[2] == 0xBC && data[3] == 0xAF && data[4] == 0x27 && data[5] == 0x1C)
        {
            return true;
        }

        return false;
    }

    private List<AssrtFileEntry> GetDownloadCandidates(
        AssrtSubtitleEntry detail,
        IReadOnlyList<string> preferredLanguages,
        SearchContext? context)
    {
        var candidates = new List<AssrtFileEntry>();
        var mainArchiveEntry = !string.IsNullOrWhiteSpace(detail.Url)
            ? new AssrtFileEntry
            {
                Url = detail.Url,
                FileName = detail.FileName ?? detail.NativeName ?? $"assrt-{detail.Id}.zip"
            }
            : null;

        // If this is an Episode request and AutoUnpackSeasonPack is enabled, prefer downloading the main archive FIRST!
        // Because:
        // 1) Assrt's /onthefly/ individual file links very frequently return HTTP 493, wasting time retrying 10+ links.
        // 2) Downloading the main archive once lets us extract the exact current episode AND batch-extract all other episodes in the season!
        var preferMainArchiveFirst = context?.ContentType == VideoContentType.Episode
            && _configuration.AutoUnpackSeasonPack
            && mainArchiveEntry != null;

        if (preferMainArchiveFirst && mainArchiveEntry != null)
        {
            candidates.Add(mainArchiveEntry);
        }

        if (detail.FileList != null)
        {
            var fileEntries = detail.FileList
                .Where(f => !string.IsNullOrWhiteSpace(f.Url))
                .ToList();

            // Filter or prioritize by episode if we know the target episode
            if (context?.EpisodeNumber.HasValue == true)
            {
                var matchingEpisodeFiles = fileEntries
                    .Where(f => IsEpisodeMatch(f.FileName, context.SeasonNumber, context.EpisodeNumber.Value))
                    .OrderByDescending(f => ScoreSubtitleFile(f.FileName ?? string.Empty, preferredLanguages, context.SeasonNumber, context.EpisodeNumber))
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Only include individual files that match our target episode (or have no episode numbers at all),
                // NEVER try to download S01E02 when the user requested S01E01!
                var nonConflictingFiles = fileEntries
                    .Where(f =>
                    {
                        var (_, ep) = TryParseSeasonEpisode(f.FileName);
                        return !ep.HasValue;
                    })
                    .OrderByDescending(f => ScoreSubtitleFile(f.FileName ?? string.Empty, preferredLanguages, context.SeasonNumber, context.EpisodeNumber))
                    .ToList();

                candidates.AddRange(matchingEpisodeFiles);
                candidates.AddRange(nonConflictingFiles);
            }
            else
            {
                candidates.AddRange(fileEntries
                    .OrderByDescending(f => ScoreSubtitleFile(f.FileName ?? string.Empty, preferredLanguages, null, null))
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase));
            }
        }

        // Ensure main archive is always included as fallback if not already added
        if (mainArchiveEntry != null && !candidates.Any(c => string.Equals(c.Url, mainArchiveEntry.Url, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(mainArchiveEntry);
        }

        return candidates;
    }

    private (MemoryStream? Stream, string? Format, string? SelectedName, int BatchSavedCount) ExtractAndAutoDeployFromArchive(
        byte[] data,
        IReadOnlyList<string> preferredLanguages,
        string resolvedLanguage,
        SearchContext? context)
    {
        var extractedFiles = ExtractAllSubtitleFilesFromArchive(data, maxDepth: 3);
        if (extractedFiles.Count == 0)
        {
            return (null, null, null, 0);
        }

        var targetSeason = context?.SeasonNumber;
        var targetEpisode = context?.EpisodeNumber;

        // 1. Select the best subtitle for the requested episode (or movie)
        var rankedForTarget = extractedFiles
            .Select(f => new
            {
                File = f,
                Score = ScoreSubtitleFile(f.FullPath, preferredLanguages, targetSeason, targetEpisode)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selected = rankedForTarget.First().File;
        _logger.LogInformation(
            "从压缩包中共提取到 {Count} 个字幕文件，为当前剧集 (S{Season:00}E{Episode:00}) 选中最佳匹配: {SelectedPath} (得分: {Score})",
            extractedFiles.Count,
            targetSeason ?? 0,
            targetEpisode ?? 0,
            selected.FullPath,
            rankedForTarget.First().Score);

        // 2. If AutoUnpackSeasonPack is enabled and we have episode context, batch-deploy subtitles for all episodes in the season!
        var batchSavedCount = 0;
        if (_configuration.AutoUnpackSeasonPack
            && context?.ContentType == VideoContentType.Episode
            && !string.IsNullOrWhiteSpace(context.MediaPath))
        {
            try
            {
                batchSavedCount = BatchDeploySeasonSubtitles(
                    extractedFiles,
                    context,
                    preferredLanguages,
                    resolvedLanguage,
                    selected);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "自动批量解压部署整季字幕时发生异常（不影响当前集字幕返回）");
            }
        }

        var output = new MemoryStream(selected.Content);
        var format = NormalizeFormatFromExtension(GetExtension(selected.FileName));
        return (output, format, selected.FileName, batchSavedCount);
    }

    private List<ExtractedSubtitleFile> ExtractAllSubtitleFilesFromArchive(byte[] archiveBytes, int maxDepth, string parentPath = "")
    {
        var results = new List<ExtractedSubtitleFile>();
        if (maxDepth <= 0 || archiveBytes.Length < 4)
        {
            return results;
        }

        try
        {
            using var ms = new MemoryStream(archiveBytes);
            using var archive = ArchiveFactory.OpenArchive(ms);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                var entryKey = entry.Key.Replace('\\', '/');
                var fullPath = string.IsNullOrEmpty(parentPath) ? entryKey : $"{parentPath}/{entryKey}";
                var fileName = Path.GetFileName(entryKey);
                if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith("._", StringComparison.Ordinal) || fullPath.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ext = GetExtension(fileName);
                if (SubtitleExtensions.Contains(ext))
                {
                    using var entryStream = new MemoryStream();
                    entry.WriteTo(entryStream);
                    var bytes = entryStream.ToArray();
                    if (bytes.Length > 0)
                    {
                        var (season, episode) = TryParseSeasonEpisode(fullPath);
                        results.Add(new ExtractedSubtitleFile
                        {
                            FileName = fileName,
                            FullPath = fullPath,
                            Content = bytes,
                            SeasonNumber = season,
                            EpisodeNumber = episode
                        });
                    }
                }
                else if (ArchiveExtensions.Contains(ext) && maxDepth > 1)
                {
                    using var nestedStream = new MemoryStream();
                    entry.WriteTo(nestedStream);
                    var nestedBytes = nestedStream.ToArray();
                    if (IsArchiveData(nestedBytes))
                    {
                        results.AddRange(ExtractAllSubtitleFilesFromArchive(nestedBytes, maxDepth - 1, fullPath));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析压缩包条目时出错 (路径: {ParentPath})", parentPath);
        }

        return results;
    }

    private int BatchDeploySeasonSubtitles(
        List<ExtractedSubtitleFile> extractedFiles,
        SearchContext context,
        IReadOnlyList<string> preferredLanguages,
        string defaultLanguageCode,
        ExtractedSubtitleFile currentSelectedFile)
    {
        var mediaPath = context.MediaPath!;
        var seasonDir = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
        {
            return 0;
        }

        var targetSeason = context.SeasonNumber;
        var currentEpisode = context.EpisodeNumber;

        // Discover all sibling video files in the same folder
        var videoFiles = Directory.GetFiles(seasonDir)
            .Where(f => VideoExtensions.Contains(GetExtension(f)))
            .ToList();

        if (videoFiles.Count <= 1)
        {
            return 0;
        }

        var episodesMap = new Dictionary<int, string>();
        foreach (var videoFile in videoFiles)
        {
            var (vSeason, vEpisode) = TryParseSeasonEpisode(Path.GetFileNameWithoutExtension(videoFile));
            if (!vEpisode.HasValue)
            {
                continue;
            }

            if (targetSeason.HasValue && vSeason.HasValue && vSeason.Value != targetSeason.Value)
            {
                continue;
            }

            episodesMap.TryAdd(vEpisode.Value, videoFile);
        }

        if (episodesMap.Count == 0)
        {
            return 0;
        }

        var savedVideoPaths = new List<string>();
        var savedEpisodeCount = 0;

        foreach (var (epNum, videoFilePath) in episodesMap.OrderBy(kv => kv.Key))
        {
            // Find all extracted subtitles matching this episode
            var matchingSubs = extractedFiles
                .Where(f => IsEpisodeMatch(f.FullPath, targetSeason, epNum))
                .OrderByDescending(f => ScoreSubtitleFile(f.FullPath, preferredLanguages, targetSeason, epNum))
                .ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchingSubs.Count == 0)
            {
                continue;
            }

            var videoDir = Path.GetDirectoryName(videoFilePath)!;
            var videoBaseName = Path.GetFileNameWithoutExtension(videoFilePath);

            // Group by distinct language variant + extension so we can save the best file for each variant (or the top preferred one)
            // For the current episode that Jellyfin is already saving via SubtitleManager, we skip writing the exact primary file
            // to avoid file-lock race conditions with Jellyfin's own SubtitleManager.
            var isCurrentEpisode = currentEpisode.HasValue && epNum == currentEpisode.Value &&
                                   string.Equals(Path.GetFullPath(videoFilePath), Path.GetFullPath(mediaPath), StringComparison.OrdinalIgnoreCase);

            var bestSub = matchingSubs.First();
            var ext = NormalizeFormatFromExtension(GetExtension(bestSub.FileName));
            var langCode = TryGuessLanguageFromFileName(bestSub.FullPath) ?? defaultLanguageCode;
            if (string.IsNullOrWhiteSpace(langCode))
            {
                langCode = "zho";
            }

            if (!isCurrentEpisode)
            {
                var destPath = Path.Combine(videoDir, $"{videoBaseName}.{langCode}.{ext}");
                try
                {
                    File.WriteAllBytes(destPath, bestSub.Content);
                    savedEpisodeCount++;
                    savedVideoPaths.Add(videoFilePath);
                    _logger.LogInformation("已自动为第 {Episode:00} 集解压并匹配字幕: {DestFile} (源自: {SourceFile})", epNum, Path.GetFileName(destPath), bestSub.FullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "写入字幕文件失败: {DestPath}", destPath);
                }
            }

            // If there is also a secondary format/variant for the preferred language (e.g., primary was .ass and there's also a .srt, or cht vs chs),
            // let's check if there is an alternative format (.srt vs .ass) for the top language so user has format choice too.
            var altFormatSub = matchingSubs
                .Skip(1)
                .FirstOrDefault(f =>
                {
                    var fExt = NormalizeFormatFromExtension(GetExtension(f.FileName));
                    var fLang = TryGuessLanguageFromFileName(f.FullPath) ?? defaultLanguageCode;
                    return !string.Equals(fExt, ext, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(fLang, langCode, StringComparison.OrdinalIgnoreCase)
                           && ScoreSubtitleFile(f.FullPath, preferredLanguages, targetSeason, epNum) >= 100;
                });

            if (altFormatSub != null && !isCurrentEpisode)
            {
                var altExt = NormalizeFormatFromExtension(GetExtension(altFormatSub.FileName));
                var altDestPath = Path.Combine(videoDir, $"{videoBaseName}.{langCode}.{altExt}");
                try
                {
                    if (!File.Exists(altDestPath))
                    {
                        File.WriteAllBytes(altDestPath, altFormatSub.Content);
                        _logger.LogInformation("已自动为第 {Episode:00} 集额外保存备用格式字幕: {DestFile}", epNum, Path.GetFileName(altDestPath));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "写入备用格式字幕失败: {AltDestPath}", altDestPath);
                }
            }
        }

        if (savedVideoPaths.Count > 0)
        {
            TriggerLibrarySubtitleRefresh(seasonDir, savedVideoPaths);
        }

        return savedEpisodeCount;
    }

    private void TriggerLibrarySubtitleRefresh(string seasonDir, List<string> updatedVideoPaths)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000).ConfigureAwait(false);

                var libraryMonitor = _serviceProvider.GetService<ILibraryMonitor>();
                libraryMonitor?.ReportFileSystemChanged(seasonDir);

                var libraryManager = _serviceProvider.GetService<ILibraryManager>();
                var directoryService = _serviceProvider.GetService<IDirectoryService>();
                var providerManager = _serviceProvider.GetService<IProviderManager>();

                if (libraryManager == null)
                {
                    return;
                }

                foreach (var videoPath in updatedVideoPaths)
                {
                    try
                    {
                        libraryMonitor?.ReportFileSystemChanged(videoPath);
                        var item = libraryManager.FindByPath(videoPath, isFolder: false);
                        if (item is Episode episode && providerManager != null && directoryService != null)
                        {
                            var refreshOptions = new MetadataRefreshOptions(directoryService)
                            {
                                MetadataRefreshMode = MetadataRefreshMode.Default,
                                ImageRefreshMode = MetadataRefreshMode.None,
                                ReplaceAllImages = false,
                                ReplaceAllMetadata = false,
                                ForceSave = false
                            };

                            providerManager.QueueRefresh(episode.Id, refreshOptions, RefreshPriority.High);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "刷新单集字幕元数据失败: {VideoPath}", videoPath);
                    }
                }

                _logger.LogInformation("已触发 Jellyfin 媒体库刷新同季 {Count} 集的外部字幕元数据", updatedVideoPaths.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "触发媒体库字幕刷新任务失败");
            }
        });
    }

    private static bool IsEpisodeMatch(string? name, int? targetSeason, int targetEpisode)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var (season, episode) = TryParseSeasonEpisode(name);
        if (!episode.HasValue || episode.Value != targetEpisode)
        {
            return false;
        }

        if (targetSeason.HasValue && season.HasValue && season.Value != targetSeason.Value)
        {
            return false;
        }

        return true;
    }

    private static (int? Season, int? Episode) TryParseSeasonEpisode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        // Normalize full-width characters and common separators
        var normalized = text
            .Replace('－', '-')
            .Replace('—', '-')
            .Replace('＿', '_');

        // 1. Standard S01E02 / S1E2 / S01.E02 / S01_E02 / S01 E02
        var seMatch = Regex.Match(normalized, @"(?<![a-zA-Z0-9])S(?<s>\d{1,2})[\s\._\-]*E(?<e>\d{1,3})(?!\d)", RegexOptions.IgnoreCase);
        if (seMatch.Success
            && int.TryParse(seMatch.Groups["s"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s1)
            && int.TryParse(seMatch.Groups["e"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e1))
        {
            return (s1, e1);
        }

        // 2. 1x02 format (avoid matching resolutions like 1920x1080)
        var xMatch = Regex.Match(normalized, @"(?<![a-zA-Z0-9\d])(?<s>\d{1,2})x(?<e>\d{2,3})(?![a-zA-Z0-9\d])", RegexOptions.IgnoreCase);
        if (xMatch.Success
            && int.TryParse(xMatch.Groups["s"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s2)
            && int.TryParse(xMatch.Groups["e"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e2)
            && s2 < 50 && e2 < 200)
        {
            return (s2, e2);
        }

        // Check if season is mentioned separately in folder/filename (e.g. "Season 1", "第一季", "S01")
        int? detectedSeason = TryParseSeasonOnly(normalized);

        // 3. EP02 / E02 / Ep.02 / Episode 02
        var epMatch = Regex.Match(normalized, @"(?<![a-zA-Z0-9])(?:EP|Episode)[\s\._\-]*(?<e>\d{1,3})(?!\d)|(?<![a-zA-Z0-9])E(?<e2>\d{2,3})(?![a-zA-Z0-9])", RegexOptions.IgnoreCase);
        if (epMatch.Success)
        {
            var eStr = epMatch.Groups["e"].Success ? epMatch.Groups["e"].Value : epMatch.Groups["e2"].Value;
            if (int.TryParse(eStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e3))
            {
                return (detectedSeason, e3);
            }
        }

        // 4. Chinese episode format: 第02集 / 第2话 / 第十二集
        var cnEpMatch = Regex.Match(normalized, @"第\s*(?<e>[0-9零〇一二两三四五六七八九十壹贰叁肆伍陆柒捌玖拾]{1,4})\s*[集话話期回]");
        if (cnEpMatch.Success)
        {
            var parsedCn = ParseChineseOrArabicNumber(cnEpMatch.Groups["e"].Value);
            if (parsedCn.HasValue)
            {
                return (detectedSeason, parsedCn.Value);
            }
        }

        // 5. Part / 1of6 / [02] / - 02 (common in anime & miniseries)
        var ofMatch = Regex.Match(normalized, @"(?<![a-zA-Z0-9])(?<e>\d{1,2})of\d{1,2}(?![a-zA-Z0-9])", RegexOptions.IgnoreCase);
        if (ofMatch.Success && int.TryParse(ofMatch.Groups["e"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eOf))
        {
            return (detectedSeason, eOf);
        }

        var fileNameOnly = Path.GetFileNameWithoutExtension(normalized);
        var bracketMatch = Regex.Match(fileNameOnly, @"\[(?<e>\d{2})\](?!\d|p|P|bit)");
        if (bracketMatch.Success && int.TryParse(bracketMatch.Groups["e"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eBracket))
        {
            if (eBracket is >= 1 and <= 99)
            {
                return (detectedSeason, eBracket);
            }
        }

        return (detectedSeason, null);
    }

    private static int? TryParseSeasonOnly(string text)
    {
        var sMatch = Regex.Match(text, @"(?<![a-zA-Z0-9])(?:S|Season[\s\._\-]*)(?<s>\d{1,2})(?![a-zA-Z0-9])", RegexOptions.IgnoreCase);
        if (sMatch.Success && int.TryParse(sMatch.Groups["s"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }

        var cnSeasonMatch = Regex.Match(text, @"第\s*(?<s>[0-9零〇一二两三四五六七八九十壹贰叁肆伍陆柒捌玖拾ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ]{1,3})\s*季");
        if (cnSeasonMatch.Success)
        {
            return ParseChineseOrArabicNumber(cnSeasonMatch.Groups["s"].Value);
        }

        // Roman numeral I / II / III at end of title
        if (text.Contains('Ⅰ'))
        {
            return 1;
        }

        if (text.Contains('Ⅱ'))
        {
            return 2;
        }

        if (text.Contains('Ⅲ'))
        {
            return 3;
        }

        return null;
    }

    private static int? ParseChineseOrArabicNumber(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim();
        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arabic))
        {
            return arabic;
        }

        var total = 0;
        var current = 0;
        foreach (var ch in input)
        {
            if (!ChineseDigitMap.TryGetValue(ch, out var val))
            {
                return null;
            }

            if (val == 10)
            {
                total += (current == 0 ? 1 : current) * 10;
                current = 0;
            }
            else
            {
                current = val;
            }
        }

        total += current;
        return total > 0 ? total : null;
    }

    private static int ScoreSubtitleFile(
        string name,
        IReadOnlyList<string> preferredLanguages,
        int? targetSeason,
        int? targetEpisode)
    {
        var extension = GetExtension(name);
        if (!SubtitleExtensions.Contains(extension) && !ArchiveExtensions.Contains(extension))
        {
            return -1000;
        }

        var score = 0;

        // 1. Episode & Season matching (highest priority!)
        if (targetEpisode.HasValue)
        {
            var (season, episode) = TryParseSeasonEpisode(name);
            if (episode.HasValue)
            {
                if (episode.Value == targetEpisode.Value)
                {
                    if (targetSeason.HasValue && season.HasValue)
                    {
                        if (season.Value == targetSeason.Value)
                        {
                            score += 200; // Exact Season + Episode match
                        }
                        else
                        {
                            score -= 500; // Wrong season! (e.g. S02E01 when looking for S01E01)
                        }
                    }
                    else
                    {
                        score += 150; // Episode matches
                    }
                }
                else
                {
                    score -= 500; // Different episode!
                }
            }
        }

        // 2. Subtitle format scoring (.ass / .ssa preferred over .srt for styling)
        if (string.Equals(extension, ".ass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".ssa", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }
        else if (string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }
        else if (SubtitleExtensions.Contains(extension))
        {
            score += 10;
        }
        else if (ArchiveExtensions.Contains(extension))
        {
            score += 5;
        }

        // 3. Language & Chinese variant scoring
        var guessedLang = TryGuessLanguageFromFileName(name);
        if (preferredLanguages.Count > 0 && guessedLang != null)
        {
            var idx = -1;
            for (var i = 0; i < preferredLanguages.Count; i++)
            {
                if (string.Equals(preferredLanguages[i], guessedLang, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx == 0)
            {
                score += 40; // Primary preferred language
            }
            else if (idx > 0)
            {
                score += 20; // Secondary preferred language
            }
            else
            {
                score -= 20; // Non-preferred language
            }
        }

        // Prefer Simplified Chinese + Bilingual (简体&英文 / chs&eng / 简英 / 简中) over Traditional Chinese (繁体 / cht)
        var lowered = name.ToLowerInvariant();
        var isSimplified = lowered.Contains("简体") || lowered.Contains("简中") || lowered.Contains("简英") ||
                           Regex.IsMatch(lowered, @"(?<![a-z])(chs|sc|gb|zh-cn|zh_cn)(?![a-z])");
        var isTraditional = lowered.Contains("繁体") || lowered.Contains("繁中") || lowered.Contains("繁英") ||
                            Regex.IsMatch(lowered, @"(?<![a-z])(cht|tc|big5|zh-tw|zh_tw|zh-hk)(?![a-z])");
        var isBilingual = lowered.Contains("双语") || lowered.Contains("中英") || lowered.Contains("简英") || lowered.Contains("繁英") ||
                          lowered.Contains("&eng") || lowered.Contains("&en") || lowered.Contains("简体&英文") || lowered.Contains("繁体&英文");

        if (isSimplified)
        {
            score += 15;
        }
        else if (isTraditional)
        {
            score += 5;
        }

        if (isBilingual)
        {
            score += 8;
        }

        return score;
    }

    private static string? TryGuessLanguageFromFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lowered = name.ToLowerInvariant();
        if (lowered.Contains("简体") || lowered.Contains("繁体") || lowered.Contains("简中") ||
            lowered.Contains("繁中") || lowered.Contains("中英") || lowered.Contains("中字") ||
            lowered.Contains("双语") ||
            Regex.IsMatch(lowered, @"(?<![a-z])(chs|cht|chinese|zh|zho|chi|sc|tc)(?![a-z])", RegexOptions.IgnoreCase))
        {
            return "zho";
        }

        if (lowered.Contains("英文") || lowered.Contains("英语") ||
            Regex.IsMatch(lowered, @"(?<![a-z])(eng|english|en)(?![a-z])", RegexOptions.IgnoreCase))
        {
            return "eng";
        }

        if (Regex.IsMatch(lowered, @"(?<![a-z])(jpn|japanese|jp)(?![a-z])", RegexOptions.IgnoreCase))
        {
            return "jpn";
        }

        if (Regex.IsMatch(lowered, @"(?<![a-z])(kor|korean|kr)(?![a-z])", RegexOptions.IgnoreCase))
        {
            return "kor";
        }

        return null;
    }

    private IReadOnlyList<string> BuildPreferredLanguageList(SubtitleSearchRequest request)
    {
        var languages = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            languages.Add(request.Language.Trim().ToLowerInvariant());
        }

        languages.AddRange(_preferredLanguages);
        return languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolveLanguage(AssrtLanguageInfo? languageInfo, IReadOnlyList<string> preferredLanguages, string? fallback)
    {
        var candidates = new List<string>();

        if (languageInfo?.LangList is not null)
        {
            foreach (var lang in languageInfo.LangList.Where(kv => kv.Value))
            {
                if (LanguageKeyMap.TryGetValue(lang.Key, out var mapped))
                {
                    candidates.Add(mapped);
                }
            }
        }

        // Try to guess from description text.
        if (!string.IsNullOrWhiteSpace(languageInfo?.Description))
        {
            var desc = languageInfo.Description.ToLowerInvariant();
            if (desc.Contains("英"))
            {
                candidates.Add("eng");
            }

            if (desc.Contains("中") || desc.Contains("简") || desc.Contains("繁"))
            {
                candidates.Add("zho");
            }

            if (desc.Contains("日"))
            {
                candidates.Add("jpn");
            }

            if (desc.Contains("韩"))
            {
                candidates.Add("kor");
            }
        }

        foreach (var preferred in preferredLanguages)
        {
            var hit = candidates.FirstOrDefault(c => string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(hit))
            {
                return hit;
            }
        }

        return candidates.FirstOrDefault() ?? fallback;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return "srt";
        }

        var lower = format.ToLowerInvariant();
        if (lower.Contains("ass"))
        {
            return "ass";
        }

        if (lower.Contains("vobsub"))
        {
            return "sub";
        }

        if (lower.Contains("ssa"))
        {
            return "ssa";
        }

        return lower.Contains("srt", StringComparison.OrdinalIgnoreCase) ? "srt" : lower;
    }

    private static string NormalizeFormatFromExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(ext) ? "srt" : ext;
    }

    private static string GetExtension(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetExtension(path);
    }

    /// <summary>
    /// Preview information and candidate search queries for a media item.
    /// </summary>
    public sealed class PreviewQueriesResult
    {
        public bool EnableCustomUi { get; set; } = true;

        public string ItemId { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? SeriesName { get; set; }

        public string? OriginalTitle { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public bool IsEpisode { get; set; }

        public List<string> TagKeywords { get; set; } = new();

        public List<string> Queries { get; set; } = new();
    }

    private sealed class CustomSearchContext
    {
        public Guid? ItemId { get; init; }

        public string? CustomQuery { get; init; }

        public bool AppendEpisode { get; init; } = true;

        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    private sealed class MediaMetadataInfo
    {
        public Guid? ItemId { get; set; }

        public string? SeriesOriginalTitle { get; set; }

        public string? OriginalTitle { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public int? ProductionYear { get; set; }

        public List<string> TagKeywords { get; } = new();

        public List<string> ConfiguredAliases { get; } = new();
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;

        public ActionDisposable(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDispose();
            }
        }
    }

    private sealed class SearchContext
    {
        public VideoContentType ContentType { get; init; }

        public string? SeriesName { get; init; }

        public int? SeasonNumber { get; init; }

        public int? EpisodeNumber { get; init; }

        public string? MediaPath { get; init; }

        public string? RequestLanguage { get; init; }

        public List<string> PreferredLanguages { get; init; } = new();

        public DateTime CreatedAt { get; init; }
    }

    private sealed class ExtractedSubtitleFile
    {
        public string FileName { get; init; } = string.Empty;

        public string FullPath { get; init; } = string.Empty;

        public byte[] Content { get; init; } = Array.Empty<byte>();

        public int? SeasonNumber { get; init; }

        public int? EpisodeNumber { get; init; }
    }
}
