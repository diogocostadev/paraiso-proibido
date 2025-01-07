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

                await ProcessDeletedEpornVideos(stoppingToken);
                
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
            await RefreshMaterializedView(stoppingToken);
        }

        _logger.LogInformation("Processamento de vídeos deletados concluído");
    }

    private async Task ProcessDeletedEpornVideos(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Iniciando processamento de vídeos deletados");
        int page = 1;
        bool hasMorePages = true;
        bool hasUpdates = false;

        while (hasMorePages && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var videos = await GetDeletedEpornVideos();
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
            await RefreshMaterializedView(stoppingToken);
        }

        _logger.LogInformation("Processamento de vídeos deletados concluído");
    }

    private async Task<List<DeletedVideo>> GetDeletedEpornVideos()
    {
        using var client = _httpClientFactory.CreateClient();
        var url = $"https://www.eporner.com/api/v2/video/removed/?format=TXT";
        
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return null;

        var content = await response.Content.ReadAsStringAsync();

        return content.Split("\n")
            .Select(o => new DeletedVideo() { VideoId = o})
            .ToList();
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
                    
                    if (updated)
                        _logger.LogInformation($"Video {video.VideoId} atualizado com sucesso");
                    else
                    {       
                        _logger.LogWarning($"Video {video.VideoId} não foi atualizado");
                    }
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

    private async Task RefreshMaterializedView(CancellationToken stoppingToken)
    {
        const int maxRetries = 3;
        var currentTry = 0;
        var baseDelay = TimeSpan.FromSeconds(5);

        while (currentTry < maxRetries)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(stoppingToken);

                // Define um timeout maior para o comando
                await using var cmd = new NpgsqlCommand
                {
                    Connection = connection,
                    CommandText = "REFRESH MATERIALIZED VIEW dev.videos_com_miniaturas_normal",
                    CommandTimeout = 3600 // 1 hora
                };

                _logger.LogInformation("Iniciando atualização da materialized view (tentativa {Attempt}/{MaxRetries})",
                    currentTry + 1, maxRetries);

                await cmd.ExecuteNonQueryAsync(stoppingToken);
                _logger.LogInformation("Materialized view atualizada com sucesso");
                return;
            }
            catch (Exception ex) when (ex is PostgresException || ex is OperationCanceledException)
            {
                currentTry++;
                if (currentTry >= maxRetries)
                {
                    _logger.LogError(ex,
                        "Falha ao atualizar materialized view após {Retries} tentativas",
                        maxRetries);
                    throw;
                }

                var delay = baseDelay * (1 << currentTry); // Exponential backoff
                _logger.LogWarning(ex,
                    "Erro ao atualizar materialized view (tentativa {Attempt}/{MaxRetries}). Tentando novamente em {Delay} segundos",
                    currentTry,
                    maxRetries,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Atualização da materialized view cancelada pelo usuário");
                    throw;
                }
            }
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