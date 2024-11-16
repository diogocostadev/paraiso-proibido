
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using paraiso.models;
using paraiso.models.Mapper;
using paraiso.models.PHub;

namespace paraiso.phub.atualiza.categorias;

public class CategoryWorker : BackgroundService
{
    private readonly ILogger<CategoryWorker> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private const string CATEGORIES_API_URL = "https://api.redtube.com/?data=redtube.Categories.getCategoriesList&output=json";
    private const string VIDEOS_API_URL = "https://api.redtube.com/?data=redtube.Videos.searchVideos&output=json&thumbsize=all&ordering=newest";

    public CategoryWorker(ILogger<CategoryWorker> logger)
    {
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
                // Primeiro verifica se existem categorias no banco
                if (!await HasCategories())
                {
                    _logger.LogInformation("Banco vazio. Iniciando sincronização inicial de categorias");
                    await SyncNewCategories();
                }

                // Processa vídeos por categoria continuando de onde parou
                await ProcessAllCategoriesContinuous(stoppingToken);

                // Aguarda 5 minutos antes da próxima verificação
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante a execução do worker");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private async Task<bool> HasCategories()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            const string sql = "SELECT COUNT(*) FROM dev.categorias";
            var count = await connection.ExecuteScalarAsync<int>(sql);
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task SyncNewCategories()
    {
        try
        {
            var response = await _httpClient.GetAsync(CATEGORIES_API_URL);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CategoriesResponse>();
            if (result?.Categories == null) return;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            foreach (var category in result.Categories)
            {
                if (!await CategoryExists(connection, category.Id))
                {
                    await InsertCategory(connection, category);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao sincronizar novas categorias");
        }
    }

    private async Task<bool> CategoryExists(NpgsqlConnection connection, int categoryId)
    {
        const string sql = "SELECT COUNT(1) FROM dev.categorias WHERE id = @Id";
        var count = await connection.ExecuteScalarAsync<int>(sql, new { Id = categoryId });
        return count > 0;
    }

    private async Task<bool> VideoExists(NpgsqlConnection connection, string videoId)
    {
        const string sql = "SELECT COUNT(1) FROM dev.videos WHERE id = @VideoId";
        var count = await connection.ExecuteScalarAsync<int>(sql, new { VideoId = videoId });
        return count > 0;
    }
    
    private async Task InsertCategory(NpgsqlConnection connection, Categoria categoria)
    {
        try
        {
            const string sql = @"
                INSERT INTO dev.categorias (id, nome)
                VALUES (@Id, @Name)
                ON CONFLICT (id) DO NOTHING";

            await connection.ExecuteAsync(sql, new { Id = categoria.Id, Name = categoria.Category });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao inserir categoria {CategoryId}", categoria.Id);
        }
    }

    private async Task ProcessAllCategoriesContinuous(CancellationToken stoppingToken)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        const string sql = @"
            SELECT c.id, c.nome, 
                   COALESCE(MAX(m.ultima_pagina), 0) as ultima_pagina
            FROM dev.categorias c
            LEFT JOIN dev.monitor_categoria_progresso m ON c.id = m.categoria_id
            GROUP BY c.id, c.nome";

        var categories = await connection.QueryAsync<(int id, string nome, int ultimaPagina)>(sql);

        // Processamento em paralelo com limite de concorrência
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 3, // Ajuste conforme necessário
            CancellationToken = stoppingToken
        };

        await Parallel.ForEachAsync(categories, parallelOptions, async (category, token) =>
        {
            await ProcessCategoryVideos(category.id, category.nome, category.ultimaPagina + 1, token);
        });
    }

    private async Task ProcessCategoryVideos(int categoryId, string categoryName, int startPage, CancellationToken stoppingToken)
    {
        int currentPage = startPage;
        bool shouldContinue = true;

        while (shouldContinue && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var url = $"{VIDEOS_API_URL}&category={Uri.EscapeDataString(categoryName)}&page={currentPage}";
                var videos = await FetchVideosFromApi(url);

                if (videos?.Videos == null || !videos.Videos.Any())
                {
                    shouldContinue = false;
                    continue;
                }

                int processedCount = await ProcessVideoCategories(videos, categoryId);
                
                // Atualiza o progresso mesmo se não processou nenhum vídeo
                await UpdateCategoryProgress(categoryId, currentPage, processedCount);
                
                currentPage++;
                await Task.Delay(1000, stoppingToken); // Delay para não sobrecarregar a API
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar categoria {CategoryName} página {Page}", 
                    categoryName, currentPage);
                shouldContinue = false;
            }
        }
    }

    private async Task<int> ProcessVideoCategories(VideosHome videosHome, int categoryId)
    {
        int processedCount = 0;
        
        if (videosHome?.Videos == null) return processedCount;

        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        foreach (var videoItem in videosHome.Videos)
        {
            NpgsqlTransaction transaction = null;
            try
            {
                transaction = await connection.BeginTransactionAsync();
                var video = videoItem.Video.MapperToModel();

                // Verifica se o vídeo existe
                if (!await VideoExists(connection, video.Id))
                {
                    // Se não existe, cria o vídeo e seus relacionamentos
                    await InsertVideo(connection, video);
                    await InsertThumbnails(connection, video.Id, video.Miniaturas);
                    await InsertTerms(connection, video.Id, video.Termos);
                }

                // Insere a relação vídeo-categoria
                if (await InsertVideoCategory(connection, video.Id, categoryId))
                {
                    processedCount++;
                }

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync();
                }
                _logger.LogError(ex, "Erro ao processar vídeo {VideoId} da categoria {CategoryId}", 
                    videoItem.Video.VideoId, categoryId);
            }
            finally
            {
                if (transaction != null)
                {
                    await transaction.DisposeAsync();
                }
            }
        }

        return processedCount;
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
        if (thumbnails == null || !thumbnails.Any()) return;

        const string insertSql = @"
            INSERT INTO dev.miniaturas (video_id, tamanho, largura, altura, src, padrao)
            VALUES (@VideoId, @Tamanho, @Largura, @Altura, @Src, @Padrao)";
            
        var thumbnailsWithVideoId = thumbnails.Select(t => new
        {
            VideoId = videoId,
            t.Tamanho,
            t.Largura,
            t.Altura,
            t.Src,
            t.Padrao
        });

        await connection.ExecuteAsync(insertSql, thumbnailsWithVideoId);
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
    
    private async Task<VideosHome> FetchVideosFromApi(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<VideosHome>();
        }
        catch
        {
            return null;
        }
    }


    private async Task<bool> InsertVideoCategory(NpgsqlConnection connection, string videoId, int categoryId)
    {
        try
        {
            const string sql = @"
                INSERT INTO dev.video_categorias (video_id, categoria_id)
                VALUES (@VideoId, @CategoryId)
                ON CONFLICT (video_id, categoria_id) DO NOTHING
                RETURNING video_id";

            var result = await connection.ExecuteScalarAsync<string>(sql, 
                new { VideoId = videoId, CategoryId = categoryId });

            return result != null; // Retorna true se inseriu novo registro
        }
        catch
        {
            return false;
        }
    }

    private async Task UpdateCategoryProgress(int categoryId, int currentPage, int processedCount)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            const string sql = @"
                INSERT INTO dev.monitor_categoria_progresso 
                    (categoria_id, ultima_pagina, ultima_atualizacao, videos_processados)
                VALUES 
                    (@CategoryId, @CurrentPage, @LastUpdate, @ProcessedCount)
                ON CONFLICT (categoria_id) DO UPDATE
                SET 
                    ultima_pagina = @CurrentPage,
                    ultima_atualizacao = @LastUpdate,
                    videos_processados = dev.monitor_categoria_progresso.videos_processados + @ProcessedCount";

            await connection.ExecuteAsync(sql, new 
            { 
                CategoryId = categoryId, 
                CurrentPage = currentPage,
                LastUpdate = DateTime.UtcNow,
                ProcessedCount = processedCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao atualizar progresso da categoria {CategoryId}", categoryId);
        }
    }
}

public class CategoriesResponse
{
    public List<Categoria> Categories { get; set; }
}

public class Categoria
{
    public int Id { get; set; }
    public string Category { get; set; }
}