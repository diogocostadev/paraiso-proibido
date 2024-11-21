using System.Diagnostics;
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
    
    
    public async Task<IActionResult> Index(int p = 1, int t = 36, string b = "", int catid = 0)
    {
        ViewBag.b = b;
        
        try
        {
            // Carrega categorias e container em paralelo
            var categoriasTask = CarregarCategorias();
            var resultadoTask = CarregarVideos(p, t, b, catid);

            await Task.WhenAll(categoriasTask, resultadoTask);
            
            ViewData["Categorias"] = categoriasTask.Result;
            return View(resultadoTask.Result);
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

    private async Task<ResultadoPaginado<VideoBase>> CarregarVideos(int pagina, int tamanhoPagina, string termo,
        int categoriaId)
    {
        ResultadoPaginado<VideoBase> resultado;
        if (!string.IsNullOrWhiteSpace(termo))
        {
            resultado = await _servicoVideos.ObterVideosPorTermoAsync(termo, pagina, tamanhoPagina);
        }
        else if (categoriaId > 0)
        {
            resultado = await _servicoVideos.ObterVideosPorCategoriaAsync(categoriaId, pagina, tamanhoPagina);
        }
        else
        {
            resultado = await _servicoVideos.ObterVideosAsync(pagina, tamanhoPagina);
        }

        return resultado;
    }

    private async Task<List<Categoria>> CarregarCategorias()
    {
        var categorias = (await _servicoVideos.ObterCategoria())
            .Where(o => o.Mostrar.HasValue && o.Mostrar.Value)
            .ToList();
            
        categorias.Add(new Categoria { Id = 0, Nome = "Todas" });
        return categorias;
    }
    
    public async Task<IActionResult> Categorias(int p = 1, int t = 36, string b = "")
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
        var resultado = await _servicoVideos.ObterVideoPorIdAsync(id);
        return View(resultado);
    }

    public IActionResult TermoDeUso()
    {
        return View();
    }
    
    public IActionResult PoliticaDePrivacidade()
    {
        return View();
    }
    
    public IActionResult AvisoConteudoSensivel()
    {
        return View();
    }
    
    public IActionResult PoliticaDeCookie()
    {
        return View();
    }
    
    public IActionResult RemocaoDeVideo()
    {
        return View();
    }

    public IActionResult TermoDeConsentimento()
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