using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinnyShare;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Credential issued at self-registration. Never the user's Jellyfin password.</summary>
    public string? Token { get; set; }

    /// <summary>Public hostname assigned to this installation.</summary>
    public string? Hostname { get; set; }

    /// <summary>Link to the console page for choosing a custom address.</summary>
    public string? RenameUrl { get; set; }

    /// <summary>Sharing switched off from the plugin, without uninstalling it.</summary>
    public bool Disabled { get; set; }
}

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml) => Instance = this;

    public static Plugin? Instance { get; private set; }

    public override string Name => Constants.PluginName;

    public override Guid Id => Guid.Parse(Constants.PluginGuid);

    /// <summary>The settings page: the address, and the two buttons that change it.</summary>
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        },
    ];
}

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // A proxy must forward a redirect, not resolve it. Left on the default, HttpClient
        // followed Jellyfin's "/" -> "/web/index.html" itself and returned the final 200, so
        // the browser stayed at "/" and every relative asset 404'd - a blank page.
        //
        // Cookies off for the same reason: one shared jar across every viewer would let one
        // viewer's session cookie be attached to another's request.
        services.AddHttpClient(Constants.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            });

        // Registered as a singleton and then handed to the hosted-service machinery, so the
        // API controller resolves the *running* tunnel rather than a second idle copy.
        services.AddSingleton<TunnelService>();
        services.AddHostedService(sp => sp.GetRequiredService<TunnelService>());
    }
}
