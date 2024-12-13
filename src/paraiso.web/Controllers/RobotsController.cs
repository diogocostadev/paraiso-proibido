using Microsoft.AspNetCore.Mvc;

namespace paraiso.web.Controllers;

public class RobotsController : Controller
{
    private readonly IConfiguration _configuration;

    public RobotsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [Route("/robots.txt")]
    public ContentResult RobotsTxt()
    {
        var baseUrl = _configuration["SiteUrl"];
        var content = $@"User-agent: *
Allow: /
Sitemap: {baseUrl}/sitemap.xml

# Disallow specific paths if needed
Disallow: /admin/
Disallow: /private/
";

        return Content(content, "text/plain");
    }
}