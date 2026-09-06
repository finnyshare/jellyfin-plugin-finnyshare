using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// The two buttons on the settings page.
///
/// They go through Jellyfin rather than letting the browser call the control plane directly,
/// so the installation credential never leaves the server. Administrator-only, because
/// changing the address or switching sharing off affects everyone using this Jellyfin.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("FinnyShare")]
public class FinnyShareController : ControllerBase
{
    private readonly TunnelService _tunnel;

    public FinnyShareController(TunnelService tunnel) => _tunnel = tunnel;

    /// <summary>Assigns a fresh random address. The old one stops working immediately.</summary>
    [HttpPost("NewAddress")]
    public async Task<ActionResult> NewAddress()
    {
        var hostname = await _tunnel.NewAddressAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return hostname is null ? Problem("not registered yet") : Ok(new { hostname });
    }

    /// <summary>Turns sharing off or on without uninstalling the plugin.</summary>
    [HttpPost("ToggleSharing")]
    public async Task<ActionResult> ToggleSharing()
    {
        var disabled = Plugin.Instance?.Configuration.Disabled ?? false;
        await _tunnel.SetDisabledAsync(!disabled, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { disabled = !disabled });
    }
}
