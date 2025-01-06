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

    private readonly List<Categoria> _categoriasMenu;
    
    public HomeController(ILogger<HomeController> logger, IHttpClientFactory httpClientFactory, ServicoVideosCache servicoVideos)
    {
        _logger = logger;
        _servicoVideos = servicoVideos;
        _httpClient = httpClientFactory.CreateClient();

        _categoriasMenu = CarregarCategorias().Result;
    }

    [Route("/")]
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
            var resultadoTask = await  _servicoVideos.ObterVideosComFiltrosAsync(
                b, duracao, periodo, ordem, p, t, catid);

            ViewData["categoriaId"] = catid;
            ViewData["termo"] = b;
            ViewData["duracao"] = duracao;
            ViewData["periodo"] = periodo;
            ViewData["ordem"] = ordem;
        
            ViewData["Categorias"] = _categoriasMenu;
            return View(resultadoTask);
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
    
    [Route("/categorias")]
    public async Task<IActionResult> Categorias(int p = 1, int t = 36, string b = "")
    {
        ViewBag.b = b;
        
        try
        {
            //Layout
            ViewData["Categorias"] = _categoriasMenu;

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

    [Route("/video/{id}")]
    public async Task<IActionResult> V(string id)
    {
        //Layout
        ViewData["Categorias"] = _categoriasMenu;
        
        //Container
        var resultado = await _servicoVideos.ObterVideoPorIdAsync(id);
        return View(resultado);
    }

    [Route("/termos-de-uso")]
    public IActionResult TermoDeUso()
    {
        return View();
    }
    
    [Route("/compliance-statement")]
    public IActionResult ComplianceStatement()
    {
        return View();
    }
    
    [Route("/politica-de-privacidade")]
    public IActionResult PoliticaDePrivacidade()
    {
        return View();
    }
    
    [Route("/aviso-conteudo-sensivel")]
    public IActionResult AvisoConteudoSensivel()
    {
        return View();
    }
    
    [Route("/politica-de-cookie")]
    public IActionResult PoliticaDeCookie()
    {
        return View();
    }
    
    [Route("/remocao-de-video")]
    public IActionResult RemocaoDeVideo()
    {
        return View();
    }

    [Route("/termo-de-consentimento")]
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