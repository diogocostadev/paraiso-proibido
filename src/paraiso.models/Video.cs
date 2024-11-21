namespace paraiso.models;

public class Video
{
    public string Id { get; set; }
    public string Titulo { get; set; }
    public int Visualizacoes { get; set; }
    public decimal Avaliacao { get; set; }
    public string Url { get; set; }
    public DateTime DataAdicionada { get; set; }
    public int DuracaoSegundos { get; set; }
    public string DuracaoMinutos { get; set; }
    public string Embed { get; set; }
    public int SiteId { get; set; }
    public string DefaultThumbSize { get; set; }
    public int DefaultThumbWidth { get; set; }
    public int DefaultThumbHeight { get; set; }
    public string DefaultThumbSrc { get; set; }
    public List<Miniatura> Miniaturas { get; set; }
    public List<Termo> Termos { get; set; }

    public Video(string id, string titulo, int visualizacoes, decimal avaliacao, string url, DateTime dataAdicionada, int duracaoSegundos, string duracaoMinutos, string embed, int siteId, string defaultThumbSize, int defaultThumbWidth, int defaultThumbHeight, string defaultThumbSrc, 
        List<Miniatura> miniaturas, List<Termo> termos)
    {
        Id = id;
        Titulo = titulo;
        Visualizacoes = visualizacoes;
        Avaliacao = avaliacao;
        Url = url;
        DataAdicionada = dataAdicionada;
        DuracaoSegundos = duracaoSegundos;
        DuracaoMinutos = duracaoMinutos;
        Embed = embed;
        SiteId = siteId;
        DefaultThumbSize = defaultThumbSize;
        DefaultThumbWidth = defaultThumbWidth;
        DefaultThumbHeight = defaultThumbHeight;
        DefaultThumbSrc = defaultThumbSrc;
        Miniaturas = miniaturas;
        Termos = termos;
    }
}

