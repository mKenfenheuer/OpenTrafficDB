using Microsoft.AspNetCore.Mvc;

namespace OpenTrafficDB.Controllers;

public class MapController : Controller
{
    /// <summary>Live traffic overlay map.</summary>
    [HttpGet("/map/traffic")]
    public IActionResult Traffic() => View();
}
