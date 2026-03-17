using Microsoft.AspNetCore.Mvc;

namespace OpenTrafficDB.App.Controllers;

[ApiExplorerSettings(IgnoreApi = true)]
public class MapController : Controller
{
    /// <summary>Live traffic overlay map.</summary>
    [HttpGet("/map/traffic")]
    public IActionResult Traffic() => View();

    /// <summary>Traffic-aware routing map.</summary>
    [HttpGet("/map/routing")]
    public IActionResult Routing() => View();

    /// <summary>Debug view: paste an encoded polyline and visualise it on the map.</summary>
    [HttpGet("/map/debug/polyline")]
    public IActionResult DebugPolyline() => View();
}
