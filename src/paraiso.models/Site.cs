namespace paraiso.models;

public record Site(
    int Id, 
    string Nome, 
    string? Dominio // Aceita nulo
);