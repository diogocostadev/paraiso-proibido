using Microsoft.AspNetCore.Mvc;

namespace paraiso.web.Controllers;

public class ErrorController : Controller
{
    [Route("Error/NotFound")]
    public new IActionResult NotFound()
    {
        Response.StatusCode = 404;
        return View("~/Views/Shared/NotFound.cshtml");
    }
}