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

    private readonly AssrtApiClient _apiClient;
    private readonly ILogger<AssrtSubtitleProvider> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SearchContext> _contextCache = new();
    private PluginConfiguration _configuration = new();
    private List<string> _preferredLanguages = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AssrtSubtitleProvider"/> class.
    /// </summary>
    public AssrtSubtitleProvider(AssrtApiClient apiClient, ILogger<AssrtSubtitleProvider> logger, IServiceProvider serviceProvider)
    {
        _apiClient = apiClient;
        _logger = logger;
        _serviceProvider = serviceProvider;
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

            _logger.LogInformation("向客户端广播通知 [{Header}]: {Text} (目标会话数: {Count})", header, text, sessions.Count);

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

        var queries = BuildQueries(request);
        if (queries.Count == 0)
        {
            _logger.LogDebug("Could not build a search query for the incoming request");
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        var preferredLanguages = BuildPreferredLanguageList(request);
        var contextKey = StoreSearchContext(request, preferredLanguages);

        var mergedEntries = new List<AssrtSubtitleEntry>();
        var seenIds = new HashSet<int>();

        foreach (var query in queries)
        {
            var results = await _apiClient.SearchAsync(token, query, cancellationToken).ConfigureAwait(false);
            foreach (var entry in results)
            {
                if (seenIds.Add(entry.Id))
                {
                    mergedEntries.Add(entry);
                }
            }

            if (mergedEntries.Count >= 15)
            {
                break;
            }
        }

        return mergedEntries.Select(entry => MapToResult(entry, preferredLanguages, request.Language, contextKey));
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (subtitleId, context) = ParseSubtitleId(id);

        var token = GetApiToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            const string tokenErr = "射手网 (Assrt) API Token 未配置，请前往控制台插件设置中配置！";
            await SendNotificationAsync("射手网字幕", "❌ " + tokenErr, 6000).ConfigureAwait(false);
            throw new InvalidOperationException(tokenErr);
        }

        await SendNotificationAsync("射手网字幕", $"⏳ 正在解析字幕 #{subtitleId} 下载信息...", 2500).ConfigureAwait(false);

        var detail = await _apiClient.GetDetailAsync(token, subtitleId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            var notFoundErr = $"未在射手网找到字幕 #{subtitleId} 的详情。";
            await SendNotificationAsync("射手网字幕", "❌ " + notFoundErr, 5000).ConfigureAwait(false);
            throw new FileNotFoundException(notFoundErr);
        }

        var preferredLanguages = context?.PreferredLanguages is { Count: > 0 } ctxLangs ? ctxLangs : _preferredLanguages;
        var language = ResolveLanguage(detail.LanguageInfo, preferredLanguages, context?.RequestLanguage);
        var title = detail.NativeName ?? detail.VideoName ?? detail.Title ?? $"Assrt #{subtitleId}";

        var candidates = GetDownloadCandidates(detail, preferredLanguages, context);
        if (candidates.Count == 0)
        {
            var noFileErr = $"字幕“{title}”没有可供下载的文件地址。";
            await SendNotificationAsync("射手网字幕", "❌ " + noFileErr, 5000).ConfigureAwait(false);
            throw new FileNotFoundException(noFileErr);
        }

        await SendNotificationAsync("射手网字幕", $"⬇️ 开始下载: {title}", 3000).ConfigureAwait(false);

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
                    await SendNotificationAsync("射手网字幕", $"🔄 正在切换备用下载源 ({i + 1}/{candidates.Count})...", 2500).ConfigureAwait(false);
                }

                _logger.LogInformation("正在从射手网下载 [{Index}/{Total}]: {FileName} ({Url})", i + 1, candidates.Count, candidate.FileName, candidate.Url);
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
                _logger.LogWarning("下载候选 {FileName} 触发 HTTP 493 (链接失效或受限)，正在尝试备用地址...", candidate.FileName);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogWarning(ex, "下载候选 {FileName} 失败: {Message}，正在尝试备用地址...", candidate.FileName, ex.Message);
            }
        }

        if (downloadedData == null || successfulFile == null)
        {
            string errMessage;
            if (lastException is HttpRequestException { StatusCode: (System.Net.HttpStatusCode)493 })
            {
                errMessage = "下载失败：射手网接口返回 493（该字幕源直链已失效或受限），请在搜索结果中换选其他字幕条目。";
            }
            else
            {
                errMessage = $"下载失败：{lastException?.Message ?? "无法获取字幕文件"}，请尝试其他字幕条目。";
            }

            await SendNotificationAsync("射手网字幕", "❌ " + errMessage, 7000).ConfigureAwait(false);
            throw new InvalidOperationException(errMessage, lastException);
        }

        var extension = GetExtension(successfulFile.FileName);
        if (ArchiveExtensions.Contains(extension) || IsArchiveData(downloadedData))
        {
            await SendNotificationAsync("射手网字幕", "📦 正在解压并智能匹配剧集字幕文件...", 2500).ConfigureAwait(false);
            var (stream, format, selectedName, batchSavedCount) = ExtractAndAutoDeployFromArchive(
                downloadedData,
                preferredLanguages,
                language ?? "zho",
                context);

            if (stream is null)
            {
                var extractErr = $"解压失败：无法从压缩包中提取有效的字幕文件 ({successfulFile.FileName})";
                await SendNotificationAsync("射手网字幕", "❌ " + extractErr, 6000).ConfigureAwait(false);
                throw new InvalidDataException(extractErr);
            }

            var fmt = format ?? "srt";
            var epInfo = context?.EpisodeNumber.HasValue == true
                ? $" (匹配: {Path.GetFileName(selectedName)})"
                : string.Empty;

            if (batchSavedCount > 0)
            {
                await SendNotificationAsync(
                    "射手网字幕",
                    $"✅ 当前集字幕已就绪 ({fmt.ToUpperInvariant()}){epInfo}，并已自动为同季其余 {batchSavedCount} 集批量解压配好字幕！",
                    8000).ConfigureAwait(false);
            }
            else
            {
                await SendNotificationAsync(
                    "射手网字幕",
                    $"✅ 字幕已成功下载并保存 ({fmt.ToUpperInvariant()}){epInfo}！请刷新页面或重新进入播放以选择新字幕。",
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
        await SendNotificationAsync("射手网字幕", $"✅ 字幕已成功下载并保存 ({directFormat.ToUpperInvariant()})！请刷新页面或重新进入播放以选择新字幕。", 6000).ConfigureAwait(false);

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
            throw new ArgumentException("字幕 ID 不能为空", nameof(id));
        }

        var parts = id.Split(':', 2);
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var subtitleId))
        {
            throw new ArgumentException("字幕 ID 必须为数字", nameof(id));
        }

        SearchContext? context = null;
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            _contextCache.TryGetValue(parts[1], out context);
        }

        return (subtitleId, context);
    }

    private static List<string> BuildQueries(SubtitleSearchRequest request)
    {
        var queries = new List<string>();

        if (request.ContentType == VideoContentType.Episode
            && !string.IsNullOrWhiteSpace(request.SeriesName)
            && request.ParentIndexNumber.HasValue
            && request.IndexNumber.HasValue)
        {
            var englishSeriesName = ExtractEnglishTitleFromPath(request.MediaPath);

            // 1. Primary query: SeriesName S01E01
            queries.Add($"{request.SeriesName} S{request.ParentIndexNumber.Value:00}E{request.IndexNumber.Value:00}");

            // 2. If MediaPath has a distinct English series name (e.g., "Wolf Hall" when SeriesName is "狼厅"), search that too
            if (!string.IsNullOrWhiteSpace(englishSeriesName)
                && !request.SeriesName.Contains(englishSeriesName, StringComparison.OrdinalIgnoreCase))
            {
                queries.Add($"{englishSeriesName} S{request.ParentIndexNumber.Value:00}E{request.IndexNumber.Value:00}");
            }

            // 3. Fallback season pack query: SeriesName S01
            queries.Add($"{request.SeriesName} S{request.ParentIndexNumber.Value:00}");
        }
        else if (!string.IsNullOrWhiteSpace(request.Name))
        {
            queries.Add(request.Name);
        }
        else if (!string.IsNullOrWhiteSpace(request.MediaPath))
        {
            queries.Add(Path.GetFileNameWithoutExtension(request.MediaPath));
        }

        return queries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractEnglishTitleFromPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(mediaPath);
        var match = Regex.Match(fileName, @"^(?<title>[A-Za-z0-9\.\s'\-]+?)(?:[\.\s\-_]+\(\d{4}\))?[\.\s\-_]+S\d{1,2}[\.\s\-_]*E\d{1,3}", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var title = match.Groups["title"].Value.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-');
            if (title.Length >= 2)
            {
                return title;
            }
        }

        return null;
    }

    private RemoteSubtitleInfo MapToResult(
        AssrtSubtitleEntry entry,
        IReadOnlyList<string> preferredLanguages,
        string? requestLanguage,
        string contextKey)
    {
        var language = ResolveLanguage(entry.LanguageInfo, preferredLanguages, requestLanguage);
        var baseName = entry.NativeName ?? entry.VideoName ?? entry.Title ?? $"Assrt #{entry.Id}";
        var format = NormalizeFormat(entry.SubType);

        // 构造富有信息量的标题与描述标签，方便在搜索列表中甄别
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
                tags.Add($"全季包({distinctEpisodes}集)");
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
            commentParts.Add($"来源: {entry.ReleaseSite}");
        }

        if (!string.IsNullOrWhiteSpace(entry.LanguageInfo?.Description))
        {
            commentParts.Add(entry.LanguageInfo.Description);
        }

        if (!string.IsNullOrWhiteSpace(entry.FileName))
        {
            commentParts.Add($"文件: {entry.FileName}");
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

    private static string? GetLanguageTag(AssrtLanguageInfo? info)
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
                return "双语";
            }

            if (d.Contains("简体") || d.Contains("简中") || d.Contains("chs", StringComparison.OrdinalIgnoreCase))
            {
                return "简中";
            }

            if (d.Contains("繁体") || d.Contains("繁中") || d.Contains("cht", StringComparison.OrdinalIgnoreCase))
            {
                return "繁中";
            }

            if (d.Contains("中英"))
            {
                return "中英";
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
