using System;
using System.Net.Http.Headers;
using Jellyfin.Plugin.AssrtSubtitles.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AssrtSubtitles;

/// <summary>
/// Registers services required by the plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Name used for the shared HTTP client.
    /// </summary>
    public const string HttpClientName = "AssrtSubtitles";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Jellyfin-Plugin-AssrtSubtitles",
                typeof(PluginServiceRegistrator).Assembly.GetName().Version?.ToString() ?? "0.3.0"));
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        serviceCollection.AddSingleton<PluginLocalizer>();
        serviceCollection.AddSingleton<AssrtApiClient>();
        serviceCollection.AddSingleton<ISubtitleProvider, AssrtSubtitleProvider>();
        serviceCollection.AddTransient<IStartupFilter, AssrtWebStartupFilter>();
    }
}
