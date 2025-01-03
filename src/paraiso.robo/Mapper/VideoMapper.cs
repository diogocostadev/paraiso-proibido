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

        return new VideosHome(videos, source.total_count);
    }

    private static Videos MapToVideos(ModelWeb.Videos source)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        
        var video = new Video(
            duration: $"{source.length_min}",
            views: source.views,
            videoId: source.id,
            rating: source.rate,
            ratings: 0, // Assumindo que não há equivalente direto
            title: source.title,
            url: source.url,
            embedUrl: source.embed,
            defaultThumb: source.default_thumb?.src ?? string.Empty,
            thumb: source.thumbs?.FirstOrDefault()?.src ?? string.Empty,
            publishDate: source.added,
            thumbs: source.thumbs?.Select(MapToThumbs).ToList() ?? new List<Thumbs>(),
            tags: source.keywords.Split(',').Select(x => new Tags(x)).ToList(),
            durationSeconds: source.length_sec, 
            2 //eporner
        );

        return new Videos(video);
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