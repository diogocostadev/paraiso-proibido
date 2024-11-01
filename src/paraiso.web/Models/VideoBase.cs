namespace paraiso.web.Models;

public class VideoBase
{
    public string Id { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    
    public int DuracaoSegundos { get; set; }
    
    public string Embed { get; set; } = string.Empty;
    
    public string DefaultThumbSize { get; set; } = string.Empty;
    
    public string DefaultThumbSrc { get; set; } = string.Empty;
}

//id, titulo, duracao_segundos, embed, default_thumb_size, default_thumb_src