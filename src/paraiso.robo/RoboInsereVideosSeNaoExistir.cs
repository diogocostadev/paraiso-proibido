using System.Net.Http.Json;
using Dapper;
using Npgsql;
using paraiso.models;
using paraiso.models.Mapper;
using paraiso.models.PHub;

namespace paraiso.robo;

public class RoboInsereVideosSeNaoExistir : BackgroundService
{
    private readonly ILogger<RoboInsereVideosSeNaoExistir> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private const string API_URL = "https://api.redtube.com/?data=redtube.Videos.searchVideos&output=json&thumbsize=all&ordering=newest&page=";

    public RoboInsereVideosSeNaoExistir(ILogger<RoboInsereVideosSeNaoExistir> logger, IConfiguration _configuration)
    {
        Console.WriteLine("-=[Atualizador de vídeos iniciado]=-");
        _logger = logger;
        _httpClient = new HttpClient();
        _connectionString = _configuration.GetConnectionString("conexao-atualiza-se-nao-existir");
    }
    
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Iniciando busca por novos vídeos: {time}", DateTimeOffset.Now);
                await SyncVideos();
                
                // Aguarda 1 hora antes da próxima sincronização
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante a busca por novos vídeos");
                // Aguarda 1 hora em caso de erro antes de tentar novamente
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }
    
    private async Task SyncVideos()
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
            
            shouldContinue = await ProcessVideos(videos, currentPage);
            
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

    private async Task<bool> ProcessVideos(VideosHome videosHome, int currentPage)
    {
        if (videosHome == null || videosHome.Videos == null || !videosHome.Videos.Any())
        {
            _logger.LogInformation("Não há mais vídeos para processar. Encerrando sincronização.");
            return false;
        }

        int monitorId = await InserirMonitoramentoCarga(currentPage);
        int videosProcessados = 0;
        int videosNovos = 0;
        int erros = 0;
        string mensagemErro = null;

        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        try
        {
            foreach (var videoItem in videosHome.Videos)
            {
                NpgsqlTransaction transaction = null;
                try
                {
                    var video = videoItem.Video.MapperToModel();
                    
                    transaction = await connection.BeginTransactionAsync();

                    if (!await VideoExists(connection, video.Id))
                    {
                        await InsertVideo(connection, video);
                        await InsertThumbnails(connection, video.Id, video.Miniaturas);
                        await InsertTerms(connection, video.Id, video.Termos);
                        videosNovos++;
                    }

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
                0, erros, mensagemErro);
            await AtualizarMetricasSync();
        }

        return true; // Continua sempre buscando novas páginas
    }

    private async Task<bool> VideoExists(NpgsqlConnection connection, string videoId)
    {
        const string sql = "SELECT COUNT(1) FROM dev.videos WHERE id = @Id";
        var count = await connection.ExecuteScalarAsync<int>(sql, new { Id = videoId });
        return count > 0;
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

    private async Task InsertThumbnails(NpgsqlConnection connection, string videoId, List<Miniatura> thumbnails)
    {
        const string insertSql = @"
            INSERT INTO dev.miniaturas (video_id, tamanho, largura, altura, src, padrao)
            VALUES (@VideoId, @Tamanho, @Largura, @Altura, @Src, @Padrao)";
            
        await connection.ExecuteAsync(insertSql, thumbnails);
    }

    private async Task InsertTerms(NpgsqlConnection connection, string videoId, List<Termo> terms)
    {
        if (terms == null || !terms.Any())
            return;

        // Insere novos termos em batch
        const string insertTermsSql = @"
            INSERT INTO dev.termos (termo)
            SELECT unnest(@Termos)
            ON CONFLICT (termo) DO NOTHING
            RETURNING id, termo";

        var termosArray = terms.Select(t => t.termo).ToArray();
        var insertedTerms = await connection.QueryAsync<(int id, string termo)>(insertTermsSql, new { Termos = termosArray });
        
        // Pega os IDs dos termos que já existiam
        const string getExistingTermsSql = @"
            SELECT id, termo 
            FROM dev.termos 
            WHERE termo = ANY(@Termos)";

        var existingTerms = (await connection.QueryAsync<(int id, string termo)>(getExistingTermsSql, new { Termos = termosArray }))
            .ToDictionary(t => t.termo, t => t.id);

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
                (@Pagina, @DataInicio, @Status, 'worker-inserir')
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