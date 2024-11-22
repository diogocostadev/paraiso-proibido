namespace paraiso.robo.ModelWeb;

public class VideoRelacionado
{
    public string Id { get; set; }
    public string Titulo { get; set; }
    public string DefaultThumbSize { get; set; }
    public List<Miniaturas> Miniaturas { get; set; }
}