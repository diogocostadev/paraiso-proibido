using System.Text.Json.Serialization;

namespace paraiso.models.PHub;

public class VideosHome
{
    public List<Videos> Videos { get; set; }
    public int Count { get; set; }
}

public class Videos
{
    public Video Video { get; set; }
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


// public class Video
// {
//     public string duration { get; set; }
//     public int views { get; set; }
//     public string video_id { get; set; }
//     public string rating { get; set; }
//     public int ratings { get; set; }
//     public string title { get; set; }
//     public string url { get; set; }
//     public string embed_url { get; set; }
//     public string default_thumb { get; set; }
//     public string thumb { get; set; }
//     public string publish_date { get; set; }
//     public Thumbs[] thumbs { get; set; }
//     public Tags[] tags { get; set; }
// }
//
// public class Thumbs
// {
//     public string size { get; set; }
//     public int width { get; set; }
//     public int height { get; set; }
//     public string src { get; set; }
// }
//
// public class Tags
// {
//     public string tag_name { get; set; }
// }
//
