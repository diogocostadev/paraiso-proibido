namespace paraiso.robo.ModelWeb;

public class VideoBase
{
    public string Id { get; set; } = string.Empty;
    public string Titulo { get; set; } = string.Empty;
    
    public int DuracaoSegundos { get; set; }
    
    public string Embed { get; set; } = string.Empty;
    
    public string DefaultThumbSize { get; set; } = string.Empty;
    
    public string DefaultThumbSrc { get; set; } = string.Empty;

    public string? DuracaoMinutos { get; set; }

    public List<Miniaturas> Miniaturas { get; set; } = new List<Miniaturas>();
    
    public List<Termos> Termos { get; set; }
    
    public List<VideoBase> VideosRelacionados { get; set; } = new List<VideoBase>();
    
    public string TituloFormatado
    {
        get
        {
            if (string.IsNullOrEmpty(Titulo))
                return "";

            if (Titulo.Length > 65)
            {
                return Titulo.Substring(0, 65).Trim() + "...";
            }

            return Titulo.Trim();
        }
    }
}
