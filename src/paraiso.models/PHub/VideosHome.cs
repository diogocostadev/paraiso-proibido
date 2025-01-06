using System.Text.Json.Serialization;

namespace paraiso.models.PHub;

public class VideosHome
{
    public List<Videos> Videos { get; set; }
    public int Count { get; set; }

    public VideosHome(List<Videos> videos, int count)
    {
        Videos = videos;
        Count = count;
    }
}

public class Videos
{
    public Video Video { get; set; }

    public Videos(Video video)
    {
        Video = video;
    }
}

public class Video
{
    public string Duration { get; set; }
    public int ? DurationSeconds { get; set; }
    public int SiteId { get; set; }
    public int Views { get; set; }
    
    [JsonPropertyName("video_id")]
    public string VideoId { get; set; }
    public string Rating { get; set; }
    public int Ratings { get; set; }
    public string Title { get; set; }
    public string Url { get; set; }
    [JsonPropertyName("embed_url")]
    public string EmbedUrl { get; set; }
    [JsonPropertyName("default_thumb")]
    public string DefaultThumb { get; set; }
    public string Thumb { get; set; }
    [JsonPropertyName("publish_date")]
    public string PublishDate { get; set; }
    public List<Thumbs> Thumbs { get; set; }
    public List<Tags> Tags { get; set; }

    public Video(string duration, int views, string videoId, string rating, int ratings, string title, string url, string embedUrl, string defaultThumb, string thumb, string publishDate, List<Thumbs> thumbs, List<Tags> tags, int durationSeconds, int _SiteId = 1)
    {
        Duration = duration;
        Views = views;
        VideoId = videoId;
        Rating = rating;
        Ratings = ratings;
        Title = title;
        Url = url;
        EmbedUrl = embedUrl;
        DefaultThumb = defaultThumb;
        Thumb = thumb;
        PublishDate = publishDate;
        Thumbs = thumbs;
        Tags = tags;
        DurationSeconds = durationSeconds;
        SiteId = _SiteId;
    }
}

public class Thumbs
{
    public string Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Src { get; set; }

    public Thumbs(string size, int width, int height, string src)
    {
        Size = size;
        Width = width;
        Height = height;
        Src = src;
    }
}

public class Tags
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; }

    public Tags(string tagName)
    {
        TagName = tagName;
    }
}