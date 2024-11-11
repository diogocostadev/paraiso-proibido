using System.Net.Http.Json;
using Dapper;
using Npgsql;
using paraiso.models;
using paraiso.models.Mapper;
using paraiso.models.PHub;

namespace WorkerService1;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private const string API_URL = "https://api.redtube.com/?data=redtube.Videos.searchVideos&output=json&thumbsize=medium&ordering=newest&page=";

    
    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _connectionString = "Host=109.199.118.135;Username=dbotprod;Password=P4r41s0Pr01b1d0;Database=videos";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Iniciando sincronização de vídeos: {time}", DateTimeOffset.Now);

                var lastSyncDate = new DateTime(2024, 10, 23); //await GetLastSyncDate();
                if (lastSyncDate == DateTime.MinValue)
                {
                    _logger.LogInformation("Nenhum vídeo encontrado no banco. Iniciando primeira sincronização.");
                }
                else
                {
                    _logger.LogInformation("Última data de sincronização: {date}", lastSyncDate);
                }
                await SyncVideos(lastSyncDate);
                
                // Aguarda 24 horas antes da próxima sincronização
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante a sincronização de vídeos");
                // Aguarda 1 hora em caso de erro antes de tentar novamente
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private async Task<DateTime> GetLastSyncDate()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        const string sql = @"
            SELECT COALESCE(MAX(data_adicionada), '0001-01-01'::timestamp) 
            FROM dev.videos";
        
        return await connection.QuerySingleAsync<DateTime>(sql);
    }
    
    private async Task SyncVideos(DateTime lastSyncDate)
    {
        int currentPage = 1;
        bool shouldContinue = true;

        while (shouldContinue)
        {
            var videos = await FetchVideosFromApi(currentPage);
            if (videos?.Videos == null || !videos.Videos.Any())
            {
                _logger.LogInformation("Nenhum vídeo encontrado na página {Page}", currentPage);
                break;
            }

            shouldContinue = await ProcessVideos(videos, lastSyncDate);
            
            if (shouldContinue)
            {
                currentPage++;
                // Pequeno delay entre as requisições para não sobrecarregar a API
                await Task.Delay(1000);
            }
        }
    }

    private async Task<VideosHome> FetchVideosFromApi(int page)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{API_URL}{page}");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<VideosHome>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar vídeos da API na página {Page}", page);
            throw;
        }
    }

    private async Task<bool> ProcessVideos(VideosHome videosHome, DateTime lastSyncDate)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        
        bool shouldContinue = true;
        
        foreach (var videoItem in videosHome.Videos)
        {
            var video = videoItem.Video.MapperToModel();
            
            // Se encontrarmos um vídeo mais antigo que o último do banco, paramos
            if (video.DataAdicionada <= lastSyncDate)
            {
                _logger.LogInformation("Encontrado vídeo mais antigo que a última sincronização: {VideoId}", video.Id);
                shouldContinue = false;
                break;
            }

            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                if (await VideoExists(connection, video.Id))
                {
                    await UpdateVideo(connection, video);
                    _logger.LogInformation("Vídeo atualizado: {VideoId}", video.Id);
                }
                else
                {
                    await InsertVideo(connection, video);
                    _logger.LogInformation("Novo vídeo inserido: {VideoId}", video.Id);
                }

                // Atualiza miniaturas e termos
                await UpdateThumbnails(connection, video.Id, video.Miniaturas);
                await UpdateTerms(connection, video.Id, video.Termos);
                
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Erro ao processar vídeo {VideoId}", video.Id);
                throw;
            }
        }

        return shouldContinue;
    }
    
    private async Task<bool> VideoExists(NpgsqlConnection connection, string videoId)
    {
        const string sql = "SELECT COUNT(1) FROM dev.videos WHERE id = @Id";
        var count = await connection.ExecuteScalarAsync<int>(sql, new { Id = videoId });
        return count > 0;
    }

    private async Task UpdateVideo(NpgsqlConnection connection, paraiso.models.Video video)
    {
        const string sql = @"
            UPDATE dev.videos 
            SET titulo = @Titulo, 
                visualizacoes = @Visualizacoes, 
                avaliacao = @Avaliacao, 
                url = @Url, 
                data_adicionada = @DataAdicionada, 
                duracao_segundos = @DuracaoSegundos, 
                duracao_minutos = @DuracaoMinutos, 
                embed = @Embed, 
                site_id = @SiteId, 
                default_thumb_size = @DefaultThumbSize, 
                default_thumb_width = @DefaultThumbWidth, 
                default_thumb_height = @DefaultThumbHeight, 
                default_thumb_src = @DefaultThumbSrc
            WHERE id = @Id";
            
        await connection.ExecuteAsync(sql, video);
    }

    private async Task InsertVideo(NpgsqlConnection connection, paraiso.models.Video video)
    {
        const string sql = @"
            INSERT INTO dev.videos 
            (id, titulo, visualizacoes, avaliacao, url, data_adicionada, 
             duracao_segundos, duracao_minutos, embed, site_id, 
             default_thumb_size, default_thumb_width, default_thumb_height, default_thumb_src)
            VALUES 
            (@Id, @Titulo, @Visualizacoes, @Avaliacao, @Url, @DataAdicionada, 
             @DuracaoSegundos, @DuracaoMinutos, @Embed, @SiteId, 
             @DefaultThumbSize, @DefaultThumbWidth, @DefaultThumbHeight, @DefaultThumbSrc)";
            
        await connection.ExecuteAsync(sql, video);
    }

    private async Task UpdateThumbnails(NpgsqlConnection connection, string videoId, List<Miniatura> thumbnails)
    {
        // Remove miniaturas existentes
        const string deleteSql = "DELETE FROM dev.miniaturas WHERE video_id = @VideoId";
        await connection.ExecuteAsync(deleteSql, new { VideoId = videoId });

        // Insere novas miniaturas
        const string insertSql = @"
            INSERT INTO dev.miniaturas (video_id, tamanho, largura, altura, src, padrao)
            VALUES (@VideoId, @Tamanho, @Largura, @Altura, @Src, @Padrao)";
            
        await connection.ExecuteAsync(insertSql, thumbnails);
    }

    private async Task UpdateTerms(NpgsqlConnection connection, string videoId, List<Termo> terms)
    {
        // Remove associações existentes
        const string deleteVideoTermsSql = "DELETE FROM dev.video_termos WHERE video_id = @VideoId";
        await connection.ExecuteAsync(deleteVideoTermsSql, new { VideoId = videoId });

        foreach (var term in terms)
        {
            // Insere ou recupera o termo
            const string termSql = @"
                WITH inserted AS (
                    INSERT INTO dev.termos (termo)
                    VALUES (@Termo)
                    ON CONFLICT (termo) DO UPDATE SET termo = EXCLUDED.termo
                    RETURNING id
                )
                SELECT id FROM inserted
                UNION ALL
                SELECT id FROM dev.termos WHERE termo = @Termo
                LIMIT 1";

            var termId = await connection.QuerySingleAsync<int>(termSql, new { Termo = term.termo });

            // Associa o termo ao vídeo
            const string videoTermSql = @"
                INSERT INTO dev.video_termos (video_id, termo_id)
                VALUES (@VideoId, @TermoId)";

            await connection.ExecuteAsync(videoTermSql, new { VideoId = videoId, TermoId = termId });
        }
    }
}