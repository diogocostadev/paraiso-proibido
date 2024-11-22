using System.Net.Http.Json;
using Dapper;
using Npgsql;
using paraiso.models;
using paraiso.models.Mapper;
using paraiso.models.PHub;
using Video = paraiso.models.Video;

namespace paraiso.robo;

public class RoboAtualizaCategorias : BackgroundService
{
    private readonly ILogger<RoboAtualizaCategorias> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;

    private const string CATEGORIES_API_URL =
        "https://api.redtube.com/?data=redtube.Categories.getCategoriesList&output=json";

    private const string VIDEOS_API_URL =
        "https://api.redtube.com/?data=redtube.Videos.searchVideos&output=json&thumbsize=all&ordering=newest";

    private const int BATCH_SIZE = 50;
    private const int MAX_RETRY_ATTEMPTS = 3;
    private const int MAX_PARALLEL_CATEGORIES = 5;

    private Dictionary<string, List<int>> categoria_falhas = new Dictionary<string, List<int>>();

    public RoboAtualizaCategorias(ILogger<RoboAtualizaCategorias> logger, IConfiguration _configuration)
    {
        _logger = logger;
        _httpClient = new HttpClient();

        var builder = new NpgsqlConnectionStringBuilder(_configuration.GetConnectionString("conexao-atualiza-base-inteira"))
        {
            Timeout = 300,
            CommandTimeout = 300,
            KeepAlive = 30,
            MaxPoolSize = 50,
            MinPoolSize = 1,
            Pooling = true
        };

        _connectionString = builder.ToString();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("-=[Iniciando resgate de categorias e vídeos - v0.0.4]=-");

                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync(stoppingToken);

                if (!await HasCategories())
                {
                    _logger.LogInformation("Banco vazio. Iniciando sincronização inicial de categorias");
                    await SyncNewCategories();
                }

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = MAX_PARALLEL_CATEGORIES,
                    CancellationToken = stoppingToken
                };

                var categories = await GetCategories(connection);

                await Parallel.ForEachAsync(categories, parallelOptions,
                    async (category, token) =>
                    {
                        await ProcessCategoryWithRetry(category.id, category.nome, category.ultimaPagina + 1, token);
                    });

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

            await using var connection = new NpgsqlConnection(_connectionString);
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

    private async Task<bool> VideoExists(NpgsqlConnection connection, string videoId, NpgsqlTransaction transaction)
    {
        const string sql = "SELECT COUNT(1) FROM dev.videos WHERE id = @VideoId";
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { VideoId = videoId },
                transaction: transaction,
                commandTimeout: 30
            ));
        return count > 0;
    }

    private async Task ProcessCategoryWithRetry(int categoryId, string categoryName, int startPage,
        CancellationToken stoppingToken)
    {
        int retryCount = 0;

        while (retryCount < MAX_RETRY_ATTEMPTS)
        {
            try
            {
                await ProcessCategoryVideos(categoryId, categoryName, startPage, stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.LogWarning(ex,
                    "Tentativa {RetryCount} de {MaxRetries} falhou para categoria {CategoryName}",
                    retryCount, MAX_RETRY_ATTEMPTS, categoryName);

                if (retryCount < MAX_RETRY_ATTEMPTS)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), stoppingToken);
                }
            }
        }
    }

    private async Task ProcessCategoryVideos(int categoryId, string categoryName, int startPage,
        CancellationToken stoppingToken)
    {
        int currentPage = startPage;
        bool shouldContinue = true;
        int totalProcessed = 0;

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"\n>> Iniciando processamento da categoria: {categoryName} (ID: {categoryId})");
        Console.WriteLine($">> Começando da página: {startPage}");
        Console.ResetColor();

        while (shouldContinue && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var url = $"{VIDEOS_API_URL}&category='{categoryName}'&page={currentPage}";
                var videos = await FetchVideosFromApi(url);

                if (videos?.Videos == null || !videos.Videos.Any())
                {
                    RegistrarFalhas(categoryName, currentPage);
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[!] Sem vídeos na página {currentPage} da categoria {categoryName}");
                    Console.ResetColor();
                    shouldContinue = DeveContinuar(categoryName, currentPage);
                    continue;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[>] Processando página {currentPage} da categoria {categoryName}");
                Console.WriteLine($"[>] Encontrados {videos.Videos.Count} vídeos");
                Console.ResetColor();

                int processedCount = await ProcessVideoCategories(videos, categoryId);
                totalProcessed += processedCount;

                await UpdateCategoryProgress(categoryId, currentPage, processedCount);

                currentPage++;

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(
                    $"[√] Página {currentPage - 1} concluída. Total processado até agora: {totalProcessed}");
                Console.ResetColor();

                await Task.Delay(1000, stoppingToken);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[X] ERRO ao processar categoria {categoryName} página {currentPage}");
                Console.WriteLine($"[X] Erro: {ex.Message}");
                Console.ResetColor();

                _logger.LogError(ex, "Erro ao processar categoria {CategoryName} página {Page}",
                    categoryName, currentPage);
                shouldContinue = false;
            }
        }

        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"\n<< Finalizado processamento da categoria: {categoryName}");
        Console.WriteLine($"<< Total de vídeos processados: {totalProcessed}");
        Console.WriteLine($"<< Páginas processadas: {currentPage - startPage}");
        Console.ResetColor();
    }


    private async Task<int> ProcessVideoCategories(VideosHome videosHome, int categoryId)
    {
        int processedCount = 0;
        int existingCount = 0;
        int newCount = 0;
        int newRelationCount = 0;
        int existingRelationCount = 0;

        if (videosHome?.Videos == null) return processedCount;

        foreach (var batch in videosHome.Videos.Chunk(BATCH_SIZE))
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            foreach (var videoItem in batch)
            {
                // Processa um vídeo por vez com sua própria transação
                await using var transaction = await connection.BeginTransactionAsync();
                try
                {
                    var video = videoItem.Video.MapperToModel();

                    // Verifica se o vídeo já existe
                    bool videoExists = await VideoExists(connection, video.Id, transaction);

                    if (!videoExists)
                    {
                        await InsertVideo(connection, video, transaction);

                        if (video.Miniaturas?.Any() == true)
                        {
                            await InsertThumbnails(connection, video.Id, video.Miniaturas, transaction);
                        }

                        if (video.Termos?.Any() == true)
                        {
                            await InsertTerms(connection, video.Id, video.Termos, transaction);
                        }

                        newCount++;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[+] Novo vídeo gravado: {video.Id} - {video.Titulo}");
                        Console.ResetColor();
                    }
                    else
                    {
                        existingCount++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[*] Vídeo já existe: {video.Id} - {video.Titulo}");
                        Console.ResetColor();
                    }

                    // Verifica e insere a relação vídeo-categoria na mesma transação
                    const string checkRelationSql = @"
                    SELECT 1 FROM dev.video_categorias 
                    WHERE video_id = @VideoId AND categoria_id = @CategoryId";

                    var relationExists = await connection.ExecuteScalarAsync<int?>(
                        new CommandDefinition(
                            checkRelationSql,
                            new { VideoId = video.Id, CategoryId = categoryId },
                            transaction: transaction,
                            commandTimeout: 30
                        ));

                    if (relationExists == null)
                    {
                        const string insertRelationSql = @"
                        INSERT INTO dev.video_categorias (video_id, categoria_id)
                        VALUES (@VideoId, @CategoryId)";

                        await connection.ExecuteAsync(
                            new CommandDefinition(
                                insertRelationSql,
                                new { VideoId = video.Id, CategoryId = categoryId },
                                transaction: transaction,
                                commandTimeout: 30
                            ));

                        newRelationCount++;
                        processedCount++;

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(
                            $"[+] Nova relação video-categoria criada: {video.Id} - Categoria: {categoryId}");
                        Console.ResetColor();
                    }
                    else
                    {
                        existingRelationCount++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(
                            $"[*] Relação video-categoria já existe: {video.Id} - Categoria: {categoryId}");
                        Console.ResetColor();
                    }

                    // Commit da transação apenas uma vez, após todas as operações
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex,
                        "Erro ao processar vídeo {VideoId} para categoria {CategoryId}",
                        videoItem.Video.VideoId, categoryId);

                    // Continue para o próximo vídeo em caso de erro
                    continue;
                }
            }

            // Log do resumo do batch
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n=== Resumo do Batch ===");
            Console.WriteLine($"Total de vídeos no batch: {batch.Length}");
            Console.WriteLine($"Novos vídeos gravados: {newCount}");
            Console.WriteLine($"Vídeos já existentes: {existingCount}");
            Console.WriteLine($"Novas relações video-categoria: {newRelationCount}");
            Console.WriteLine($"Relações video-categoria já existentes: {existingRelationCount}");
            Console.WriteLine($"Total processado com sucesso: {processedCount}");
            Console.WriteLine("=====================\n");
            Console.ResetColor();
        }

        return processedCount;
    }

    private async Task<bool> InsertVideoCategory(NpgsqlConnection connection, string videoId, int categoryId)
    {
        try
        {
            // Primeiro verifica se a relação já existe
            const string checkSql = @"
            SELECT 1 FROM dev.video_categorias 
            WHERE video_id = @VideoId AND categoria_id = @CategoryId";

            var exists = await connection.ExecuteScalarAsync<int?>(checkSql, 
                new { VideoId = videoId, CategoryId = categoryId });

            if (exists != null)
            {
                _logger.LogInformation(
                    "Relação vídeo-categoria já existe. VideoId: {VideoId}, CategoryId: {CategoryId}", 
                    videoId, categoryId);
                return false;
            }

            // Se não existe, insere
            const string insertSql = @"
            INSERT INTO dev.video_categorias (video_id, categoria_id)
            VALUES (@VideoId, @CategoryId)";

            await connection.ExecuteAsync(insertSql, 
                new { VideoId = videoId, CategoryId = categoryId });

            _logger.LogInformation(
                "Nova relação vídeo-categoria inserida. VideoId: {VideoId}, CategoryId: {CategoryId}", 
                videoId, categoryId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, 
                "Erro ao inserir relação vídeo-categoria. VideoId: {VideoId}, CategoryId: {CategoryId}", 
                videoId, categoryId);
            throw; // Propaga o erro para ser tratado na transação
        }
    }
    private void RegistrarFalhas(string categoria, int pagina)
    {
        if (categoria_falhas.ContainsKey(categoria))
        {
            categoria_falhas[categoria].Add(pagina);
        }
        else
        {
            categoria_falhas.Add(categoria, new List<int> { pagina });
        }
    }

    private bool DeveContinuar(string categoria, int pagina)
    {
        if (categoria_falhas.ContainsKey(categoria))
        {
            return categoria_falhas[categoria].Count < 10;
        }

        return true;
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

    private async Task InsertVideo(NpgsqlConnection connection, Video video, NpgsqlTransaction transaction)
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

        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                video,
                transaction: transaction,
                commandTimeout: 60
            ));
    }
    private async Task InsertThumbnails(NpgsqlConnection connection, string videoId, List<Miniatura> thumbnails, NpgsqlTransaction transaction)
    {
        if (thumbnails == null || !thumbnails.Any()) return;

        try
        {
            // Primeiro remove miniaturas existentes
            const string deleteSql = "DELETE FROM dev.miniaturas WHERE video_id = @VideoId";
            await connection.ExecuteAsync(
                new CommandDefinition(
                    deleteSql,
                    new { VideoId = videoId },
                    transaction: transaction,
                    commandTimeout: 30
                ));

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

            await connection.ExecuteAsync(
                new CommandDefinition(
                    insertSql,
                    thumbnailsWithVideoId,
                    transaction: transaction,
                    commandTimeout: 60
                ));

            _logger.LogInformation("Miniaturas inseridas com sucesso para o vídeo: {VideoId}", videoId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao inserir miniaturas para o vídeo {VideoId}", videoId);
            throw;
        }
    }

    private async Task InsertTerms(NpgsqlConnection connection, string videoId, List<Termo> terms,
        NpgsqlTransaction transaction)
    {
        if (terms == null || !terms.Any()) return;

        try
        {
            // Insere ou atualiza os termos
            const string insertTermsSql = @"
            INSERT INTO dev.termos (termo)
            SELECT unnest(@Termos)
            ON CONFLICT (termo) DO NOTHING
            RETURNING id, termo";

            var termosArray = terms.Select(t => t.termo).ToArray();

            // Busca todos os termos (tanto os que acabaram de ser inseridos quanto os que já existiam)
            const string getTermsSql = @"
            SELECT id, termo 
            FROM dev.termos 
            WHERE termo = ANY(@Termos)";

            var existingTerms = (await connection.QueryAsync<(int id, string termo)>(
                new CommandDefinition(
                    getTermsSql,
                    new { Termos = termosArray },
                    transaction: transaction,
                    commandTimeout: 30
                ))).ToDictionary(t => t.termo, t => t.id);

            // Remove relações antigas
            const string deleteOldRelationsSql = "DELETE FROM dev.video_termos WHERE video_id = @VideoId";
            await connection.ExecuteAsync(
                new CommandDefinition(
                    deleteOldRelationsSql,
                    new { VideoId = videoId },
                    transaction: transaction,
                    commandTimeout: 30
                ));

            // Insere novas relações
            const string insertRelationsSql = @"
            INSERT INTO dev.video_termos (video_id, termo_id)
            VALUES (@VideoId, @TermoId)";

            var videoTerms = terms
                .Where(t => existingTerms.ContainsKey(t.termo))
                .Select(t => new
                {
                    VideoId = videoId,
                    TermoId = existingTerms[t.termo]
                });

            await connection.ExecuteAsync(
                new CommandDefinition(
                    insertRelationsSql,
                    videoTerms,
                    transaction: transaction,
                    commandTimeout: 60
                ));

            _logger.LogInformation("Termos inseridos com sucesso para o vídeo: {VideoId}", videoId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao inserir termos para o vídeo {VideoId}", videoId);
            throw;
        }
    }

    private async Task<VideosHome> FetchVideosFromApi(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var videos = await response.Content.ReadFromJsonAsync<VideosHome>();

            if (videos?.Videos == null || !videos.Videos.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[!] A Url não retornou vídeos - tentando novamente em 30 segundos");
                Console.WriteLine($"[!] URL: {url}");
                Console.ResetColor();

                await Task.Delay(TimeSpan.FromSeconds(30));
                videos = await FetchVideosFromApi(url);

                if (videos?.Videos == null || !videos.Videos.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[X] A Url não retornou vídeos após retry - tentaremos na próxima execução");
                    Console.WriteLine($"[X] URL: {url}");
                    Console.ResetColor();
                }
            }

            return videos;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[X] ERRO ao buscar vídeos da API");
            Console.WriteLine($"[X] URL: {url}");
            Console.WriteLine($"[X] Erro: {ex.Message}");
            Console.ResetColor();

            _logger.LogError(ex, "Erro ao buscar vídeos da API: {Url}", url);
            return null;
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

    private async Task<IEnumerable<(int id, string nome, int ultimaPagina)>> GetCategories(NpgsqlConnection connection)
    {
        const string sql = @"
            SELECT c.id, c.nome, 
                   COALESCE(MAX(m.ultima_pagina), 0) as ultima_pagina
            FROM dev.categorias c
            LEFT JOIN dev.monitor_categoria_progresso m ON c.id = m.categoria_id
            GROUP BY c.id, c.nome
            ORDER BY RANDOM()";

        return await connection.QueryAsync<(int id, string nome, int ultimaPagina)>(sql);
    }


}

// Models/CategoriesResponse.cs
public class CategoriesResponse
{
    public List<Categoria> Categories { get; set; }
}

// Models/Categoria.cs
public class Categoria
{
    public int Id { get; set; }
    public string Category { get; set; }
}