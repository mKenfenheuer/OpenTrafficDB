using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace OpenTrafficDB.Controllers;

[Route("docs")]
[Authorize(AuthenticationSchemes = "Cookies")]
[ApiExplorerSettings(IgnoreApi = true)]
public class DocsController : Controller
{
    [HttpGet("")]
    [AllowAnonymous]
    [HttpGet("getting-started")]
    public IActionResult GettingStarted()
    {
        ViewData["DocsPage"] = "getting-started";
        return View();
    }

    [HttpGet("api-access")]
    [AllowAnonymous]
    public IActionResult ApiAccess()
    {
        ViewData["DocsPage"] = "api-access";
        return View();
    }

    [HttpGet("valhalla")]
    [AllowAnonymous]
    public IActionResult Valhalla()
    {
        ViewData["DocsPage"] = "valhalla";
        return View();
    }

    [HttpGet("examples")]
    [AllowAnonymous]
    public IActionResult Examples()
    {
        ViewData["DocsPage"] = "examples";
        return View();
    }
}
