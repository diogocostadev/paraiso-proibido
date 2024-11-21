namespace paraiso.web.Middleware;

public class VerificaMais18
{
    private readonly RequestDelegate _next;

    public VerificaMais18(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Verificar se é uma requisição para recursos estáticos ou API
        if (!IsStaticFile(context.Request.Path) && !context.Request.Path.StartsWithSegments("/api"))
        {
            // Verificar se o cookie existe
            if (!context.Request.Cookies.ContainsKey("AgeVerified"))
            {
                // Se for uma requisição AJAX, retornar status 401
                if (context.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }
        }

        await _next(context);
    }

    private bool IsStaticFile(PathString path)
    {
        string[] staticExtensions = { ".css", ".js", ".jpg", ".png", ".gif", ".ico", ".svg" };
        return staticExtensions.Any(ext => path.Value.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
