using System.Text;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using paraiso.web.Models;
using paraiso.web.Services;

namespace paraiso.web.Controllers;

public class SitemapController : Controller 
{
    private readonly ServicoVideosCache _servicoVideos;
    private readonly IConfiguration _configuration;
    private const int MAX_URLS_PER_SITEMAP = 45000; // Limite Google é 50.000

    public SitemapController(ServicoVideosCache servicoVideos, IConfiguration configuration)
    {
        _servicoVideos = servicoVideos;
        _configuration = configuration;
    }

    [Route("/sitemap.xml")]
    public async Task<IActionResult> Index()
    {
        var baseUrl = _configuration["SiteUrl"];
        var sb = new StringBuilder();
        
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        // Sitemap Principal (home e categorias)
        sb.AppendLine("<sitemap>");
        sb.AppendLine($"<loc>{baseUrl}/sitemap-main.xml</loc>");
        sb.AppendLine($"<lastmod>{DateTime.UtcNow:yyyy-MM-dd}</lastmod>");
        sb.AppendLine("</sitemap>");

        // Sitemaps de vídeos
        int totalVideos = await GetTotalVideosCount();
        int totalSitemaps = (int)Math.Ceiling((double)totalVideos / MAX_URLS_PER_SITEMAP);

        for (int i = 1; i <= totalSitemaps; i++)
        {
            sb.AppendLine("<sitemap>");
            sb.AppendLine($"<loc>{baseUrl}/sitemap-videos-{i}.xml</loc>");
            sb.AppendLine($"<lastmod>{DateTime.UtcNow:yyyy-MM-dd}</lastmod>");
            sb.AppendLine("</sitemap>");
        }

        sb.AppendLine("</sitemapindex>");
        return Content(sb.ToString(), "application/xml");
    }

    private async Task<int> GetTotalVideosCount()
    {
        using var conexao = new NpgsqlConnection(_configuration.GetConnectionString("conexao-site"));
        await conexao.OpenAsync();

        using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM dev.videos_com_miniaturas_normal", conexao);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
    
    [Route("/sitemap-main.xml")]
    public async Task<IActionResult> MainSitemap()
    {
        // Home e categorias
        var baseUrl = _configuration["SiteUrl"];
        var sb = new StringBuilder();
        
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        // Home page
        sb.AppendLine($"<url><loc>{baseUrl}</loc><priority>1.0</priority></url>");

        // Categorias
        var categorias = await _servicoVideos.ObterCategoria();
        foreach (var categoria in categorias.Where(c => c.Mostrar == true))
        {
            sb.AppendLine($"<url><loc>{baseUrl}/categorias?catid={categoria.Id}</loc><priority>0.8</priority></url>");
        }

        sb.AppendLine("</urlset>");
        return Content(sb.ToString(), "application/xml");
    }

    [Route("/sitemap-videos-{page}.xml")]
    public async Task<IActionResult> VideosSitemap(int page)
    {
        var baseUrl = _configuration["SiteUrl"];
        var offset = (page - 1) * MAX_URLS_PER_SITEMAP;
        
        // Aqui você precisará criar um novo método no seu serviço que retorne 
        // apenas IDs e datas dos vídeos com paginação eficiente
        var videos = await _servicoVideos.ObterVideosSitemapAsync(offset, MAX_URLS_PER_SITEMAP);

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

        foreach (var video in videos)
        {
            sb.AppendLine("<url>");
            sb.AppendLine($"<loc>{baseUrl}/v/{video.Id}</loc>");
            sb.AppendLine($"<lastmod>{video.DataAdicionada:yyyy-MM-dd}</lastmod>");
            sb.AppendLine("<priority>0.6</priority>");
            sb.AppendLine("</url>");
        }

        sb.AppendLine("</urlset>");
        return Content(sb.ToString(), "application/xml");
    }
}