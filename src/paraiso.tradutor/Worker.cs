using AutoGen.Core;
using AutoGen.Ollama;
using AutoGen.Ollama.Extension;
using Npgsql;

namespace paraiso.tradutor;

public class CategoryNameTranslator : BackgroundService
{
    private readonly ILogger<CategoryNameTranslator> _logger;

    private readonly string _connectionString = "Host=109.199.118.135;Username=dbotprod;Password=P4r41s0Pr01b1d0;Database=videos";
    private readonly string _ollamaModelName = "llama3.2:latest";

    public CategoryNameTranslator(ILogger<CategoryNameTranslator> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("CategoryNameTranslator running at: {time}", DateTimeOffset.Now);
            }

            await UpdateCategoryNames();
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task UpdateCategoryNames()
    {
        // Connect to the PostgreSQL database
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();

            // Read category names in English from the "dev.categorias" table
            string selectQuery = "SELECT id, nome FROM dev.categorias";
            await using (var command = new NpgsqlCommand(selectQuery, connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int id = reader.GetInt32(0);
                    string nomeEn = reader.GetString(1);

                    // Translate the name to Portuguese using AutoGen.Ollama
                    string nomePt = await TranslateToPortuguese(nomeEn);

                    // Update the "nome_pt" column in the table
                    string updateQuery = "UPDATE dev.categorias SET nome_pt = @nomePt WHERE id = @id";
                    await using (var updateCommand = new NpgsqlCommand(updateQuery, connection))
                    {
                        updateCommand.Parameters.AddWithValue("@nomePt", nomePt);
                        updateCommand.Parameters.AddWithValue("@id", id);
                        int rowsAffected = await updateCommand.ExecuteNonQueryAsync();
                        if (rowsAffected > 0)
                        {
                            _logger.LogInformation(
                                $"Updated category with ID {id}. English name: {nomeEn}, Portuguese name: {nomePt}");
                        }
                    }
                }
            }
        }
    }

    private async Task<string> TranslateToPortuguese(string textToTranslate)
    {
        // Create an OllamaAgent instance to handle the translation
        using (var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434") })
        {
            var agent = new OllamaAgent(
                httpClient: httpClient,
                name: "translationAgent",
                modelName: _ollamaModelName,
                systemMessage:
                "Você é um especialista em traduzir textos de sites de vídeos de lingua inglesa para o português brasileiro (pt-BR). Forneça a tradução em português para o texto dado.");

            // Send the text to translate and get the Portuguese response
            var response = await agent.SendAsync(textToTranslate);
            return response.ToString();
        }
    }
}