using System.Resources;
using paraiso.models.PHub;
using paraiso.robo.ModelWeb;
using Thumbs = paraiso.models.PHub.Thumbs;
using Videos = paraiso.models.PHub.Videos;

namespace paraiso.robo.Mapper;

public static class VideoMapper
{
    public static VideosHome MapToVideosHome(this VideoHomeEporn source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        var videos = source.videos.Select(MapToVideos).ToList();

        return new VideosHome() { Videos = videos, Count = source.total_count };
    }

    private static Videos MapToVideos(ModelWeb.Videos source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        
        var video = new Video() {
            Duration = $"{source.length_min}",
            Views = source.views,
            VideoId = source.id,
            Rating = source.rate,
            Ratings = 0, // Assumindo que não há equivalente direto
            Title =  source.title,
            Url = source.url,
            EmbedUrl = source.embed,
            DefaultThumb = source.default_thumb?.src ?? string.Empty,
            Thumb = source.thumbs?.FirstOrDefault()?.src ?? string.Empty,
            PublishDate = source.added,
            Thumbs = source.thumbs?.Select(MapToThumbs).ToList() ?? new List<Thumbs>(),
            Tags = source.keywords.Split(',').Select(x => new Tags(x)).ToList(),
            DurationSeconds = source.length_sec, 
            SiteId = 2 //eporner
            }
        ;

        return new Videos { Video = video };
    }

    private static Thumbs MapToThumbs(ModelWeb.Thumbs source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        return new Thumbs(
            size: source.size,
            width: source.width,
            height: source.height,
            src: source.src
        );
    }
}