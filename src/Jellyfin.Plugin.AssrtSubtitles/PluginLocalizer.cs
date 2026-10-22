using System;
using System.Globalization;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Supported UI locales for the Assrt Subtitles plugin.
/// </summary>
public enum PluginLocale
{
    /// <summary>
    /// Simplified Chinese (zh-CN, zh-Hans, zh-SG).
    /// </summary>
    ZhHans,

    /// <summary>
    /// Traditional Chinese (zh-TW, zh-HK, zh-MO, zh-Hant).
    /// </summary>
    ZhHant,

    /// <summary>
    /// English and other fallback locales.
    /// </summary>
    En
}

/// <summary>
/// Provides localized strings synchronized with Jellyfin's server UI culture.
/// </summary>
public class PluginLocalizer
{
    private readonly IServerConfigurationManager? _configurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLocalizer"/> class.
    /// </summary>
    /// <param name="configurationManager">Jellyfin server configuration manager.</param>
    public PluginLocalizer(IServerConfigurationManager? configurationManager = null)
    {
        _configurationManager = configurationManager;
    }

    /// <summary>
    /// Resolves the active <see cref="PluginLocale"/> from Jellyfin's server configuration or system culture.
    /// </summary>
    public PluginLocale GetCurrentLocale()
    {
        var uiCulture = _configurationManager?.Configuration?.UICulture;
        if (string.IsNullOrWhiteSpace(uiCulture))
        {
            uiCulture = _configurationManager?.Configuration?.PreferredMetadataLanguage;
        }

        if (string.IsNullOrWhiteSpace(uiCulture))
        {
            uiCulture = CultureInfo.CurrentUICulture.Name;
        }

        return ParseLocale(uiCulture);
    }

    /// <summary>
    /// Parses a culture string (e.g., "zh-CN", "zh-TW", "en-US") into <see cref="PluginLocale"/>.
    /// </summary>
    public static PluginLocale ParseLocale(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return PluginLocale.En;
        }

        var lower = culture.Trim().ToLowerInvariant();
        if (lower.StartsWith("zh", StringComparison.Ordinal) || lower.StartsWith("chi", StringComparison.Ordinal) || lower.StartsWith("zho", StringComparison.Ordinal))
        {
            if (lower.Contains("tw", StringComparison.Ordinal) ||
                lower.Contains("hk", StringComparison.Ordinal) ||
                lower.Contains("mo", StringComparison.Ordinal) ||
                lower.Contains("hant", StringComparison.Ordinal) ||
                lower.Contains("cht", StringComparison.Ordinal))
            {
                return PluginLocale.ZhHant;
            }

            return PluginLocale.ZhHans;
        }

        return PluginLocale.En;
    }

    public string NotificationHeader => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "Assrt 射手网字幕",
        PluginLocale.ZhHant => "Assrt 射手網字幕",
        _ => "Assrt Subtitles"
    };

    public string ErrSubtitleIdNotNumeric => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "字幕 ID 必须为数字",
        PluginLocale.ZhHant => "字幕 ID 必須為數字",
        _ => "Subtitle ID must be numeric"
    };

    public string ErrSubtitleIdEmpty => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "字幕 ID 不能为空",
        PluginLocale.ZhHant => "字幕 ID 不能為空",
        _ => "Subtitle ID cannot be empty"
    };

    public string ErrTokenNotConfigured => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "Assrt API 密钥未配置，请前往控制台插件设置中配置。",
        PluginLocale.ZhHant => "Assrt API 金鑰未設定，請前往控制台外掛程式設定中設定。",
        _ => "Assrt API token is not configured. Please configure it in the plugin settings."
    };

    public string MsgFetchingSubtitleDetail(int subtitleId) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"⏳ 正在获取字幕 #{subtitleId} 详情...",
        PluginLocale.ZhHant => $"⏳ 正在取得字幕 #{subtitleId} 詳情...",
        _ => $"⏳ Fetching subtitle #{subtitleId} details..."
    };

    public string ErrSubtitleDetailNotFound(int subtitleId) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"未在 Assrt 找到字幕 #{subtitleId} 的详情。",
        PluginLocale.ZhHant => $"未在 Assrt 找到字幕 #{subtitleId} 的詳情。",
        _ => $"Subtitle #{subtitleId} details not found on Assrt."
    };

    public string ErrNoDownloadableFiles(string title) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"字幕“{title}”没有可供下载的文件地址。",
        PluginLocale.ZhHant => $"字幕「{title}」沒有可供下載的檔案位址。",
        _ => $"Subtitle \"{title}\" has no downloadable file URLs."
    };

    public string MsgDownloadingSubtitle(string title) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"⬇️ 正在下载字幕: {title}",
        PluginLocale.ZhHant => $"⬇️ 正在下載字幕: {title}",
        _ => $"⬇️ Downloading subtitle: {title}"
    };

    public string MsgSwitchingFallbackCandidate(int current, int total) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"🔄 正在切换备用下载源 ({current}/{total})...",
        PluginLocale.ZhHant => $"🔄 正在切換備用下載來源 ({current}/{total})...",
        _ => $"🔄 Trying fallback download source ({current}/{total})..."
    };

    public string ErrDownloadHttp493 => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "下载失败：服务器返回 HTTP 493（链接已失效或受限），请在搜索结果中换选其他字幕。",
        PluginLocale.ZhHant => "下載失敗：伺服器傳回 HTTP 493（連結已失效或受限），請在搜尋結果中改選其他字幕。",
        _ => "Download failed: HTTP 493 (link expired or rate-limited). Please try another subtitle entry."
    };

    public string ErrDownloadGeneral(string? detail) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"下载失败：{detail ?? "无法获取字幕文件"}，请尝试其他字幕条目。",
        PluginLocale.ZhHant => $"下載失敗：{detail ?? "無法取得字幕檔案"}，請嘗試其他字幕項目。",
        _ => $"Download failed: {detail ?? "Unable to retrieve subtitle file"}. Please try another entry."
    };

    public string MsgExtractingAndMatching => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "📦 正在解压并匹配剧集字幕文件...",
        PluginLocale.ZhHant => "📦 正在解壓縮並配對劇集字幕檔案...",
        _ => "📦 Extracting archive and matching episode subtitles..."
    };

    public string ErrExtractFailed(string fileName) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"解压失败：无法从压缩包中提取有效的字幕文件 ({fileName})",
        PluginLocale.ZhHant => $"解壓縮失敗：無法從壓縮檔中擷取有效的字幕檔案 ({fileName})",
        _ => $"Extraction failed: no valid subtitle files found in archive ({fileName})"
    };

    public string MsgMatchedFileSuffix(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        return GetCurrentLocale() switch
        {
            PluginLocale.ZhHans => $"（匹配: {fileName}）",
            PluginLocale.ZhHant => $"（配對: {fileName}）",
            _ => $" (Matched: {fileName})"
        };
    }

    public string MsgSuccessWithBatchUnpack(string formatUpper, string matchedSuffix, int batchSavedCount) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"✅ 当前集字幕已就绪 ({formatUpper}){matchedSuffix}，并已自动为同季其余 {batchSavedCount} 集解压配好字幕！",
        PluginLocale.ZhHant => $"✅ 目前集數字幕已就緒 ({formatUpper}){matchedSuffix}，並已自動為同季其餘 {batchSavedCount} 集解壓配好字幕！",
        _ => $"✅ Subtitle saved ({formatUpper}){matchedSuffix}, and auto-extracted subtitles for {batchSavedCount} other episodes in this season!"
    };

    public string MsgSuccessSingle(string formatUpper, string matchedSuffix = "") => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"✅ 字幕已成功下载并保存 ({formatUpper}){matchedSuffix}！请刷新或重新进入播放以加载新字幕。",
        PluginLocale.ZhHant => $"✅ 字幕已成功下載並儲存 ({formatUpper}){matchedSuffix}！請重新整理或重新進入播放以載入新字幕。",
        _ => $"✅ Subtitle downloaded and saved ({formatUpper}){matchedSuffix}! Refresh or reopen playback to select it."
    };

    public string TagBilingual => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "双语",
        PluginLocale.ZhHant => "雙語",
        _ => "Bilingual"
    };

    public string TagSimplifiedChinese => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "简中",
        PluginLocale.ZhHant => "簡中",
        _ => "CHS"
    };

    public string TagTraditionalChinese => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "繁中",
        PluginLocale.ZhHant => "繁中",
        _ => "CHT"
    };

    public string TagChsEng => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => "中英",
        PluginLocale.ZhHant => "中英",
        _ => "ZH&EN"
    };

    public string TagSeasonPack(int count) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"全季包({count}集)",
        PluginLocale.ZhHant => $"全季包({count}集)",
        _ => $"Season Pack ({count} eps)"
    };

    public string CommentSource(string source) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"来源: {source}",
        PluginLocale.ZhHant => $"來源: {source}",
        _ => $"Source: {source}"
    };

    public string CommentFile(string fileName) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"文件: {fileName}",
        PluginLocale.ZhHant => $"檔案: {fileName}",
        _ => $"File: {fileName}"
    };

    public string CommentMatchedQuery(string query) => GetCurrentLocale() switch
    {
        PluginLocale.ZhHans => $"搜索词: {query}",
        PluginLocale.ZhHant => $"搜尋詞: {query}",
        _ => $"Query: {query}"
    };
}
