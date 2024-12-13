using Microsoft.AspNetCore.Mvc;
using paraiso.web.Models;
using paraiso.web.Services;

namespace paraiso.web.Controllers;

public class ErrorController : Controller
{
    private readonly ILogger<ErrorController> _logger;
    private readonly HttpClient _httpClient;
    private readonly ServicoVideosCache _servicoVideos;

    public ErrorController(ILogger<ErrorController> logger, IHttpClientFactory httpClientFactory, ServicoVideosCache servicoVideos)
    {
        _logger = logger;
        _servicoVideos = servicoVideos;
        _httpClient = httpClientFactory.CreateClient();
    }
    
    
    public async Task<IActionResult> Index(
        string duracao, 
        string periodo, 
        string ordem,
        int p = 1, 
        int t = 36, 
        string b = "", 
        int catid = 0)
    {
        ViewBag.b = b;
    
        try
        {
            var categoriasTask = await CarregarCategorias();
            
            ViewData["categoriaId"] = catid;
            ViewData["termo"] = b;
            ViewData["duracao"] = duracao;
            ViewData["periodo"] = periodo;
            ViewData["ordem"] = ordem;
        
            ViewData["Categorias"] = categoriasTask;
            return View();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao carregar vídeos");
            return View(new ResultadoPaginado<VideoBase>
            (
            ));
        }
    }
    
    private async Task<List<Categoria>> CarregarCategorias()
    {
        var categorias = (await _servicoVideos.ObterCategoria())
            .Where(o => o.Mostrar.HasValue && o.Mostrar.Value)
            .ToList();
            
        categorias.Add(new Categoria { Id = 0, Nome = "Todas" });
        return categorias;
    }

    
    [Route("Error/NotFound")]
    public new IActionResult NotFound()
    {
        Response.StatusCode = 404;
        return View("~/Views/Shared/NotFound.cshtml");
    }
}