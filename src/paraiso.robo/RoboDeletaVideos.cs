using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace paraiso.robo;

public class RoboDeletaVideos : BackgroundService
{
    private readonly ILogger<RoboDeletaVideos> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _intervalMinutes = 30;

    public RoboDeletaVideos(ILogger<RoboDeletaVideos> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _connectionString = _configuration.GetConnectionString("conexao-delete");
        _semaphore = new SemaphoreSlim(1, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _semaphore.WaitAsync(stoppingToken);
                await ProcessDeletedVideos(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar vídeos deletados");
            }
            finally
            {
                _semaphore.Release();
            }

            await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken);
        }
    }

    private async Task ProcessDeletedVideos(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Iniciando processamento de vídeos deletados");
        int page = 1;
        bool hasMorePages = true;
        bool hasUpdates = false;

        while (hasMorePages && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var videos = await GetDeletedVideos(page);
                if (videos?.Any() != true)
                {
                    hasMorePages = false;
                    continue;
                }

                hasUpdates = await ProcessVideoBatch(videos) || hasUpdates;
                page++;

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); // Delay entre páginas
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar página {Page}", page);
                hasMorePages = false;
            }
        }

        if (hasUpdates)
        {
            await RefreshMaterializedView();
        }

        _logger.LogInformation("Processamento de vídeos deletados concluído");
    }

    private async Task<List<DeletedVideo>> GetDeletedVideos(int page)
    {
        using var client = _httpClientFactory.CreateClient();
        var url = $"https://api.redtube.com/?data=redtube.Videos.getDeletedVideos&output=json&page={page}";
        
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<DeletedVideosResponse>(content);
        return result?.Videos;
    }

    private async Task<bool> ProcessVideoBatch(List<DeletedVideo> videos)
    {
        bool hasUpdates = false;
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var video in videos)
            {
                // Tenta inserir na tabela de deletados
                var inserted = await InsertDeletedVideo(connection, video.VideoId, transaction);
                
                if (inserted)
                {
                    // Atualiza o status do vídeo para inativo
                    var updated = await UpdateVideoStatus(connection, video.VideoId, transaction);
                    hasUpdates = hasUpdates || updated;
                }
            }

            await transaction.CommitAsync();
            return hasUpdates;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Erro ao processar vídeos deletados");
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task<bool> InsertDeletedVideo(NpgsqlConnection connection, string videoId, NpgsqlTransaction transaction)
    {
        var sql = @"
            INSERT INTO dev.videos_deletados (video_id)
            VALUES (@VideoId)
            ON CONFLICT (video_id) DO NOTHING
            RETURNING video_id";

        using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@VideoId", videoId);

        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    private async Task<bool> UpdateVideoStatus(NpgsqlConnection connection, string videoId, NpgsqlTransaction transaction)
    {
        var sql = @"
            UPDATE dev.videos 
            SET ativo = false 
            WHERE id = @VideoId AND ativo = true
            RETURNING id";

        using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("@VideoId", videoId);

        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    private async Task RefreshMaterializedView()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            using var cmd = new NpgsqlCommand(
                "REFRESH MATERIALIZED VIEW dev.videos_com_miniaturas", 
                connection);
            
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Materialized view atualizada com sucesso");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao atualizar materialized view");
        }
    }
}

public class DeletedVideosResponse
{
    [JsonPropertyName("count")]
    public string Count { get; set; }

    [JsonPropertyName("videos")]
    public List<DeletedVideo> Videos { get; set; }
}

public class DeletedVideo
{
    [JsonPropertyName("video_id")]
    public string VideoId { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("embed_url")]
    public string EmbedUrl { get; set; }
}