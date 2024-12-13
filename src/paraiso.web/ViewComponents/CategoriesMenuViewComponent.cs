using Microsoft.AspNetCore.Mvc;
using paraiso.web.Models;
using paraiso.web.Services;

namespace paraiso.web.ViewComponents;

public class CategoriesMenuViewComponent : ViewComponent
{
    private readonly ServicoVideosCache _servicoVideos;

    public CategoriesMenuViewComponent(ServicoVideosCache servicoVideos)
    {
        _servicoVideos = servicoVideos;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var categorias = (await _servicoVideos.ObterCategoria())
            .Where(o => o.Mostrar.HasValue && o.Mostrar.Value)
            .ToList();
            
        categorias.Add(new Categoria { Id = 0, Nome = "Todas" });
        
        return View(categorias);
    }
}