namespace paraiso.web.Models;

public record VideosHome(
    Videos[] videos,
    int count
);

public record Videos(
    Video video
);

public record Video(
    string duration,
    int views,
    string video_id,
    string rating,
    int ratings,
    string title,
    string url,
    string embed_url,
    string default_thumb,
    string thumb,
    string publish_date,
    Thumbs[] thumbs,
    Tags[] tags
);

public record Thumbs(
    string size,
    int width,
    int height,
    string src
);

public record Tags(
    string tag_name
);

