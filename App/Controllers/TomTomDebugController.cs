using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace OpenTrafficDB.App.Controllers;

/// <summary>
/// Admin-only debug map that shows TomTom's own traffic flow next to the OpenTrafficDB
/// traffic tiles, to compare coverage and speeds of the imported data.
///
/// TomTom tiles are proxied through the server so the TomTom API key never reaches the
/// browser. Every proxied tile counts against the daily tile quota of the key that the
/// importer also uses (its 10 % buffer), so tiles are cached for 5 minutes.
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "WebAdminOnly")]
public class TomTomDebugController(
    IHttpClientFactory httpClientFactory,
    IDistributedCache cache,
    IConfiguration config,
    ILogger<TomTomDebugController> logger) : Controller
{
    private static readonly HashSet<string> FlowStyles =
        ["relative0-dark", "relative0", "relative", "relative-dark", "absolute", "relative-delay", "reduced-sensitivity"];

    private static readonly DistributedCacheEntryOptions TileCacheOptions =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };

    /// <summary>Side-by-side comparison map: TomTom flow (left) and OpenTrafficDB traffic (right).</summary>
    [HttpGet("/map/debug/tomtom")]
    public IActionResult Index()
    {
        ViewData["TomTomConfigured"] = !string.IsNullOrEmpty(config["TomTom:ApiKey"]);
        return View("~/Views/Map/DebugTomTom.cshtml");
    }

    /// <summary>Proxies a TomTom raster flow tile (256 px PNG).</summary>
    [HttpGet("/map/debug/tomtom/flow/{style}/{z:int}/{x:int}/{y:int}.png")]
    public async Task<IActionResult> FlowTile(string style, int z, int x, int y, CancellationToken ct)
    {
        var apiKey = config["TomTom:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "TomTom:ApiKey is not configured.");

        if (!FlowStyles.Contains(style) || z is < 0 or > 22 || x < 0 || y < 0 || x >= 1 << z || y >= 1 << z)
            return BadRequest();

        var cacheKey = $"tomtom-debug:{style}:{z}:{x}:{y}";
        var png = await cache.GetAsync(cacheKey, ct);
        if (png == null)
        {
            var client = httpClientFactory.CreateClient();
            var url = $"https://api.tomtom.com/traffic/map/4/tile/flow/{style}/{z}/{x}/{y}.png" +
                      $"?tileSize=256&key={Uri.EscapeDataString(apiKey)}";
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TomTom debug tile {Style}/{Z}/{X}/{Y} returned {Status}", style, z, x, y, (int)response.StatusCode);
                return StatusCode((int)response.StatusCode);
            }

            png = await response.Content.ReadAsByteArrayAsync(ct);
            await cache.SetAsync(cacheKey, png, TileCacheOptions, ct);
        }

        Response.Headers.CacheControl = "private, max-age=300";
        return File(png, "image/png");
    }
}
