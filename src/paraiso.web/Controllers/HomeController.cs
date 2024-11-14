using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using paraiso.web.Models;
using paraiso.web.Services;

namespace paraiso.web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly HttpClient _httpClient;
    private readonly ServicoVideosCache _servicoVideos;
    public HomeController(ILogger<HomeController> logger, IHttpClientFactory httpClientFactory, ServicoVideosCache servicoVideos)
    {
        _logger = logger;
        _servicoVideos = servicoVideos;
        _httpClient = httpClientFactory.CreateClient();
    }
    
    public async Task<IActionResult> Index(int p = 1, int t = 120, string b = "",int catid = 0)
    {
        ViewBag.b = b;
        
        try
        {
            //Layout
            var categorias = (await _servicoVideos.ObterCategoria()).Where(o => o.Mostrar.HasValue && o.Mostrar.Value).ToList();
            categorias.Add(new Categoria() { Id = 0, Nome = "Todas" });
            ViewData["Categorias"] = categorias;

            //Container
            ResultadoPaginado<VideoBase> resultado;
            if (!string.IsNullOrWhiteSpace(b))
            {
                resultado = await _servicoVideos.ObterVideosPorTermoAsync(b, p, t);
            }
            else if (catid > 0)
            {
                resultado = await _servicoVideos.ObterVideosPorCategoriaAsync(catid, p, t);
            }
            else
            {
                resultado = await _servicoVideos.ObterVideosAsync(p, t);
            }
            return View(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao carregar vídeos");
            return View(new ResultadoPaginado<VideoBase>
            {
                Itens = new List<VideoBase>(),
                PaginaAtual = p,
                TamanhoPagina = t
            });
        }
    }
    
    public async Task<IActionResult> Categorias(int p = 1, int t = 120, string b = "")
    {
        ViewBag.b = b;
        
        try
        {
            //Layout
            var categorias = (await _servicoVideos.ObterCategoria()).Where(o => o.Mostrar.HasValue && o.Mostrar.Value).ToList();
            categorias.Add(new Categoria() { Id = 0, Nome = "Todas" });
            ViewData["Categorias"] = categorias;

            //Container
            ResultadoPaginado<VideoBase> resultado;
            if (!string.IsNullOrWhiteSpace(b))
            {
                resultado = await _servicoVideos.ObterVideosPorTermoAsync(b, p, t);
            }
            else
            {
                resultado = await _servicoVideos.ObterVideosAsync(p, t);
            }
            return View(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao carregar vídeos");
            return View(new ResultadoPaginado<VideoBase>
            {
                Itens = new List<VideoBase>(),
                PaginaAtual = p,
                TamanhoPagina = t
            });
        }
    }

    public async Task<IActionResult> V(string id, int p)
    {
        //Layout
        var categorias = (await _servicoVideos.ObterCategoria()).Where(o => o.Mostrar.HasValue && o.Mostrar.Value).ToList();
        categorias.Add(new Categoria() { Id = 0, Nome = "Todas" });
        ViewData["Categorias"] = categorias;
        
        //Container
        var resultado = await _servicoVideos.ObterVideoPorIdAsync(id, p);
        return View(resultado);
    }

    public async Task<IActionResult> TermoDeUso()
    {
        return View();
    }
    
    public async Task<IActionResult> PoliticaDePrivacidade()
    {
        return View();
    }
    
    public async Task<IActionResult> AvisoConteudoSensivel()
    {
        return View();
    }
    
    public async Task<IActionResult> PoliticaDeCookie()
    {
        return View();
    }
    
    public async Task<IActionResult> RemocaoDeVideo()
    {
        return View();
    }

    public async Task<IActionResult> TermoDeConsentimento()
    {
        return View();
    }
    
    public async Task<IActionResult> DetalhesEstrelas()
    {
        // Buscar detalhes das estrelas na API e enviar para a view
        var response = await _httpClient.GetAsync("https://api.redtube.com/?data=redtube.Stars.getStarList&output=json");
        if (response.IsSuccessStatusCode)
        {
            var estrelas = await response.Content.ReadAsStringAsync();
            ViewData["Estrelas"] = estrelas;
        }
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}