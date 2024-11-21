namespace paraiso.models;

public record Miniatura(
    string VideoId, 
    string Tamanho, 
    int Largura, 
    int Altura, 
    string Src, 
    bool Padrao
);