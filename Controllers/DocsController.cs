using Microsoft.AspNetCore.Mvc;

namespace OpenTrafficDB.Controllers;

[Route("docs")]
public class DocsController : Controller
{
    [HttpGet("")]
    [HttpGet("getting-started")]
    public IActionResult GettingStarted()
    {
        ViewData["DocsPage"] = "getting-started";
        return View();
    }

    [HttpGet("api-access")]
    public IActionResult ApiAccess()
    {
        ViewData["DocsPage"] = "api-access";
        return View();
    }

    [HttpGet("valhalla")]
    public IActionResult Valhalla()
    {
        ViewData["DocsPage"] = "valhalla";
        return View();
    }

    [HttpGet("examples")]
    public IActionResult Examples()
    {
        ViewData["DocsPage"] = "examples";
        return View();
    }
}
