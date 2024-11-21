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
    private const string API_URL = "https://api.redtube.com/?data=redtube.Videos.searchVideos&output=json&thumbsize=all&ordering=newest&page=";

    public Worker(ILogger<Worker> logger)
    {
        Console.WriteLine("-=[Atualizador de vídeos iniciado]=-");
        _logger = logger;
        _httpClient = new HttpClient();
        _connectionString = "Host=212.56.47.25;Username=dbotprod;Password=P4r41s0Pr01b1d0;Database=videos";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Iniciando sincronização de vídeos: {time}", DateTimeOffset.Now);

                var lastSyncDate =  new DateTime(1980, 1, 1); //await GetLastSyncDate();
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
            
            shouldContinue = await ProcessVideos(videos, lastSyncDate, currentPage);
            
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
            
            var result = await response.Content.ReadFromJsonAsync<VideosHome>();
            
            if (result?.Videos?.Count == 0 && result?.Count > 0)
            {
                _logger.LogInformation("Fim da paginação detectado. Count: {Count}", result.Count);
                return null;
            }

            return result;
            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar vídeos da API na página {Page}", page);
            throw;
        }
    }

    private async Task<bool> ProcessVideos(VideosHome videosHome, DateTime lastSyncDate, int currentPage)
    {
        if (videosHome == null || videosHome.Videos == null || !videosHome.Videos.Any())
        {
            _logger.LogInformation("Não há mais vídeos para processar. Encerrando sincronização.");
            return false;
        }

        int monitorId = await InserirMonitoramentoCarga(currentPage);
        int videosProcessados = 0;
        int videosNovos = 0;
        int videosAtualizados = 0;
        int erros = 0;
        string mensagemErro = null;

        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        bool shouldContinue = true;

        try
        {
            foreach (var videoItem in videosHome.Videos)
            {
                NpgsqlTransaction transaction = null;
                try
                {
                    var video = videoItem.Video.MapperToModel();

                    if (video.DataAdicionada <= lastSyncDate)
                    {
                        _logger.LogInformation("Encontrado vídeo mais antigo que a última sincronização: {VideoId}", video.Id);
                        shouldContinue = false;
                        break;
                    }

                    transaction = await connection.BeginTransactionAsync();

                    if (await VideoExists(connection, video.Id))
                    {
                        await UpdateVideo(connection, video);
                        videosAtualizados++;
                    }
                    else
                    {
                        await InsertVideo(connection, video);
                        videosNovos++;
                    }

                    await UpdateThumbnails(connection, video.Id, video.Miniaturas);
                    await UpdateTerms(connection, video.Id, video.Termos);

                    await transaction.CommitAsync();
                    videosProcessados++;
                    Console.WriteLine($"Página: {currentPage} - Video: {videosProcessados}");
                }
                catch (Exception ex)
                {
                    if (transaction != null)
                    {
                        await transaction.RollbackAsync();
                    }

                    erros++;
                    _logger.LogError(ex, "Erro ao processar vídeo {VideoId}",
                        videoItem?.Video?.VideoId ?? "ID desconhecido");
                }
                finally
                {
                    if (transaction != null)
                    {
                        await transaction.DisposeAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            mensagemErro = ex.Message;
            _logger.LogError(ex, "Erro durante o processamento da página {Page}", currentPage);
        }
        finally
        {
            await AtualizarMonitoramentoCarga(monitorId, videosProcessados, videosNovos,
                videosAtualizados, erros, mensagemErro);
            await AtualizarMetricasSync();
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
        if (terms == null || !terms.Any())
            return;

        // Remove associações existentes primeiro
        const string deleteVideoTermsSql = "DELETE FROM dev.video_termos WHERE video_id = @VideoId";
        await connection.ExecuteAsync(deleteVideoTermsSql, new { VideoId = videoId });

        // Pega todos os termos existentes de uma vez para evitar múltiplas queries
        const string getExistingTermsSql = @"
        SELECT id, termo 
        FROM dev.termos 
        WHERE termo = ANY(@Termos)";

        var termosArray = terms.Select(t => t.termo).ToArray();
        var existingTerms =
            (await connection.QueryAsync<(int id, string termo)>(getExistingTermsSql, new { Termos = termosArray }))
            .ToDictionary(t => t.termo, t => t.id);

        // Identifica termos que precisam ser inseridos
        var newTerms = terms.Where(t => !existingTerms.ContainsKey(t.termo))
            .Select(t => t.termo)
            .ToList();

        if (newTerms.Any())
        {
            // Insere novos termos em batch
            const string insertTermsSql = @"
            INSERT INTO dev.termos (termo)
            SELECT unnest(@Termos)
            ON CONFLICT (termo) DO NOTHING
            RETURNING id, termo";

            var insertedTerms =
                await connection.QueryAsync<(int id, string termo)>(insertTermsSql, new { Termos = newTerms });
            foreach (var term in insertedTerms)
            {
                existingTerms[term.termo] = term.id;
            }
        }

        // Prepara as associações vídeo-termo
        var videoTerms = terms.Select(t => new
        {
            VideoId = videoId,
            TermoId = existingTerms[t.termo]
        }).ToList();

        // Insere todas as associações de uma vez
        const string videoTermSql = @"
        INSERT INTO dev.video_termos (video_id, termo_id)
        SELECT unnest(@VideoIds), unnest(@TermoIds)";

        await connection.ExecuteAsync(videoTermSql, new
        {
            VideoIds = videoTerms.Select(vt => vt.VideoId).ToArray(),
            TermoIds = videoTerms.Select(vt => vt.TermoId).ToArray()
        });
    }
    
    private async Task<int> InserirMonitoramentoCarga(int pagina)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        const string sql = @"
            INSERT INTO dev.monitor_carga_videos 
                (pagina, data_inicio, status, quem)
            VALUES 
                (@Pagina, @DataInicio, @Status, 'worker-1x')
            RETURNING id";

        return await connection.QuerySingleAsync<int>(sql, new
        {
            Pagina = pagina,
            DataInicio = DateTime.UtcNow,
            Status = "em_andamento"
        });
    }

    private async Task AtualizarMonitoramentoCarga(int monitorId, int videosProcessados, int videosNovos, 
        int videosAtualizados, int erros = 0, string mensagemErro = null)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        const string sql = @"
            UPDATE dev.monitor_carga_videos 
            SET videos_processados = @VideosProcessados,
                videos_novos = @VideosNovos,
                videos_atualizados = @VideosAtualizados,
                erros = @Erros,
                data_fim = @DataFim,
                duracao_segundos = EXTRACT(EPOCH FROM (@DataFim - data_inicio))::INTEGER,
                status = @Status,
                mensagem_erro = @MensagemErro
            WHERE id = @Id";

        await connection.ExecuteAsync(sql, new
        {
            Id = monitorId,
            VideosProcessados = videosProcessados,
            VideosNovos = videosNovos,
            VideosAtualizados = videosAtualizados,
            Erros = erros,
            DataFim = DateTime.UtcNow,
            Status = mensagemErro == null ? "concluido" : "erro",
            MensagemErro = mensagemErro
        });
    }

    private async Task AtualizarMetricasSync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        const string sql = @"
            INSERT INTO dev.monitor_metricas_sync (
                data_medicao,
                total_videos,
                media_videos_por_minuto,
                paginas_processadas,
                tempo_medio_por_pagina,
                ultima_sincronizacao,
                proxima_sincronizacao
            )
            SELECT 
                NOW() as data_medicao,
                (SELECT COUNT(*) FROM dev.videos) as total_videos,
                (
                    SELECT 
                        ROUND(AVG(videos_processados::numeric / NULLIF(duracao_segundos, 0) * 60), 2)
                    FROM dev.monitor_carga_videos
                    WHERE data_inicio >= NOW() - INTERVAL '1 hour'
                ) as media_videos_por_minuto,
                (
                    SELECT COUNT(DISTINCT pagina) 
                    FROM dev.monitor_carga_videos 
                    WHERE data_inicio >= NOW() - INTERVAL '24 hours'
                ) as paginas_processadas,
                (
                    SELECT ROUND(AVG(duracao_segundos::numeric), 2)
                    FROM dev.monitor_carga_videos
                    WHERE data_inicio >= NOW() - INTERVAL '1 hour'
                    AND status = 'concluido'
                ) as tempo_medio_por_pagina,
                (
                    SELECT MAX(data_fim)
                    FROM dev.monitor_carga_videos
                    WHERE status = 'concluido'
                ) as ultima_sincronizacao,
                NOW() + INTERVAL '24 hours' as proxima_sincronizacao";

        await connection.ExecuteAsync(sql);
    }
}