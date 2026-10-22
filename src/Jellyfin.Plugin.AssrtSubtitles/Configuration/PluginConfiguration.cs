using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AssrtSubtitles.Configuration;

/// <summary>
/// Plugin configuration for the Assrt subtitle provider.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the API token used to talk to assrt.net.
    /// </summary>
    public string ApiToken { get; set; } = "tNjXZUnOJWcHznHDyalNMYqqP6IdDdpQ";

    /// <summary>
    /// Gets or sets the preferred subtitle languages (ISO 639-3 codes).
    /// </summary>
    public List<string> PreferredLanguages { get; set; } = new() { "zho", "eng" };

    /// <summary>
    /// Gets or sets a value indicating whether to automatically unpack and match full-season subtitle archives to all episodes in the same season folder.
    /// </summary>
    public bool AutoUnpackSeasonPack { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to inject the custom search keyword input box into Jellyfin Web's subtitle search dialog.
    /// </summary>
    public bool EnableCustomSearchUi { get; set; } = true;

    /// <summary>
    /// Gets or sets custom keyword alias mappings (one per line, format: SourceName=TargetKeyword).
    /// </summary>
    public string CustomKeywordMappings { get; set; } = string.Empty;
}
