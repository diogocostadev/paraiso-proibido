namespace paraiso.models.Mapper;

public static class Video
{
    public static paraiso.models.Video MapperToModel(this paraiso.models.PHub.Video video)
    {
        DateTime.TryParse(video.PublishDate, out var dataCad);
        decimal.TryParse(video.Rating, out var avalia);
        
        return new paraiso.models.Video( 
           video.VideoId,
           video.Title,
           video.Views,
           avalia, // Converte o rating para decimal, valor padrão 0 se falhar
           video.Url,
           dataCad, // Converte a data de publicação, valor padrão se falhar
           ConvertDurationToSeconds(video.Duration), // Converte a duração para segundos
           video.Duration, // Mantém a duração no formato original
           video.EmbedUrl,
           1, // Defina aqui sua lógica de SiteId, ou mantenha como valor padrão
           video.Thumbs.FirstOrDefault()?.Size ?? "medium", // Usa a primeira miniatura como padrão
           video.Thumbs.FirstOrDefault()?.Width ?? 0,
           video.Thumbs.FirstOrDefault()?.Height ?? 0,
           
           video.DefaultThumb,
           video.Thumbs.MaptoThums(video), // Mapeia o thumbnail padrão
           video.Tags.MaptoTermos() // Mapeia os termos
        );
        
      
    }

    // Método auxiliar para converter a duração em minutos e segundos (formato MM:SS) para segundos
    private static int ConvertDurationToSeconds(string duration)
    {
        var parts = duration.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes) && int.TryParse(parts[1], out var seconds))
        {
            return (minutes * 60) + seconds;
        }

        return 0; // Retorna 0 se a conversão falhar
    }

    private static List<Miniatura> MaptoThums(this List<paraiso.models.PHub.Thumbs> thumbs, paraiso.models.PHub.Video video)
    {
        List<Miniatura> tBase = new List<Miniatura>();
        foreach (var tsite in thumbs)
        {
            tBase.Add(
                new Miniatura(
                    video.VideoId,
                    tsite.Size,
                    tsite.Width,
                    tsite.Height,
                    tsite.Src,
                    tsite.Src == video.DefaultThumb)
            );
        }
        return tBase;
    }
    private static List<Termo> MaptoTermos(this List<paraiso.models.PHub.Tags> tags)
    {
        List<Termo> tBase = new List<Termo>();
        foreach (var tsite in tags)
        {
            tBase.Add(new Termo() { termo = tsite.TagName });
        }
        return tBase;
    }
}