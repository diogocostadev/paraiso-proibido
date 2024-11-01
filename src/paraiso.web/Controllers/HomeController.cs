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
    
    public async Task<IActionResult> Index(int p = 1, int t = 120, string b = "")
    {
        try
        {
            // Buscar categorias na API e enviar para a view
            var response = await _httpClient.GetAsync("https://api.redtube.com/?data=redtube.Categories.getCategoriesList&output=json");
            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync();
            
                var jsonDocument = JsonDocument.Parse(jsonResponse);
                var categoriasJson = jsonDocument.RootElement.GetProperty("categories").GetRawText();
                var categorias = JsonSerializer.Deserialize<List<Models.Categoria>>(categoriasJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                ViewData["Categorias"] = categorias;
            }
            
            var resultado = await _servicoVideos.ObterVideosAsync(p, t);
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