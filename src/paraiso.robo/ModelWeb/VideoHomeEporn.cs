namespace paraiso.robo.ModelWeb;

public class VideoHomeEporn
{
    public int count { get; set; }
    public int start { get; set; }
    public int per_page { get; set; }
    public int page { get; set; }
    public int time_ms { get; set; }
    public int total_count { get; set; }
    public int total_pages { get; set; }
    public List<Videos> videos { get; set; }
}

public class Videos
{
    public string id { get; set; }
    public string title { get; set; }
    public string keywords { get; set; }
    public int views { get; set; }
    public string rate { get; set; }
    public string url { get; set; }
    public string added { get; set; }
    public int length_sec { get; set; }
    public string length_min { get; set; }
    public string embed { get; set; }
    public Default_thumb default_thumb { get; set; }
    public Thumbs[] thumbs { get; set; }
}

public class Default_thumb
{
    public string size { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public string src { get; set; }
}

public class Thumbs
{
    public string size { get; set; }
    public int width { get; set; }
    public int height { get; set; }
    public string src { get; set; }
}

