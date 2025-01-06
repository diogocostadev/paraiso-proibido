using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using NpgsqlTypes;
using paraiso.web.Models;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace paraiso.web.Services;

public class ServicoVideosCache
{
    private readonly IMemoryCache _memoryCache;
    private readonly TimeSpan _memoryCacheDuration = TimeSpan.FromSeconds(30);

    private readonly IDistributedCache _cache;
    private readonly ILogger<ServicoVideosCache> _logger;
    private readonly string _stringConexao;
    private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private Timer _cacheWarmingTimer;

    private bool _usarCompressao = true;
    private int _tempoExpiracaoMinutos = 5;
    private int _tempoSlidingMinutos = 1;

    public Func<ResultadoPaginado<VideoBase>, Task> _onPaginaCacheada;

    public ServicoVideosCache(
        IDistributedCache cache,
        ILogger<ServicoVideosCache> logger,
        IConfiguration _configuration,
        IHostApplicationLifetime applicationLifetime, IMemoryCache memoryCache)
    {
        _cache = cache;
        _memoryCache = memoryCache;

        _logger = logger;
        _stringConexao = _configuration.GetConnectionString("conexao-site");
        _applicationLifetime = applicationLifetime;

        // Configuração do Circuit Breaker
        _circuitBreaker = Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, duration) =>
                {
                    _logger.LogWarning(
                        $"Circuit Breaker aberto por {duration.TotalSeconds} segundos devido a: {ex.Message}");
                },
                onReset: () => { _logger.LogInformation("Circuit Breaker resetado"); });

        // Iniciar Cache Warming
        //IniciarCacheWarming();
    }


    public async Task<ResultadoPaginado<VideoBase>> ObterVideosComFiltrosAsync(
        string b,
        string duracao,
        string periodo,
        string ordem,
        int pagina = 1,
        int tamanhoPagina = 36,
        int categoriaId = 0)
    {
        string chaveCache = $"videos_filtrados_{b}_{duracao}_{periodo}_{ordem}_{pagina}_{tamanhoPagina}_{categoriaId}";

        try
        {
            if (_memoryCache?.TryGetValue<ResultadoPaginado<VideoBase>>(chaveCache, out var resultadoMemoria) == true)
            {
                return resultadoMemoria;
            }

            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                using var conexao = new NpgsqlConnection(_stringConexao);
                await conexao.OpenAsync();

                var consultaBase = @"
                WITH filtered_videos AS (
                    SELECT v.* 
                    FROM dev.videos_com_miniaturas_normal v
                    WHERE 1=1
                    -- Filtro de duração
                    AND CASE 
                        WHEN @DuracaoFiltro = '0-10' THEN duracao_segundos <= 600
                        WHEN @DuracaoFiltro = '10-20' THEN duracao_segundos > 600 AND duracao_segundos <= 1200
                        WHEN @DuracaoFiltro = '20-30' THEN duracao_segundos > 1200 AND duracao_segundos <= 1800
                        WHEN @DuracaoFiltro = '30+' THEN duracao_segundos > 1800
                        ELSE TRUE
                    END
                    -- Filtro de período
                    AND CASE 
                        WHEN @PeriodoFiltro = 'today' THEN data_adicionada::date = CURRENT_DATE
                        WHEN @PeriodoFiltro = 'week' THEN data_adicionada >= (CURRENT_DATE - INTERVAL '7 days')
                        WHEN @PeriodoFiltro = 'month' THEN data_adicionada >= (CURRENT_DATE - INTERVAL '30 days')
                        ELSE TRUE
                    END
                    -- Filtro de busca
                    AND CASE 
                        WHEN @Busca <> '' THEN LOWER(titulo) LIKE '%' || LOWER(@Busca) || '%'
                        ELSE TRUE
                    END
                    -- Filtro de categoria
                    AND CASE 
                        WHEN @CategoriaId > 0 THEN 
                            EXISTS (SELECT 1 FROM dev.video_categorias vc 
                                   WHERE vc.video_id = v.id AND vc.categoria_id = @CategoriaId)
                        ELSE TRUE
                    END
                ),
                ordered_videos AS (
                    SELECT *, 1 as ordem_tipo FROM filtered_videos
                    WHERE @Ordem IN ('newest', 'oldest')
                    UNION ALL
                    SELECT *, 2 as ordem_tipo FROM filtered_videos
                    WHERE @Ordem IN ('longest', 'shortest')
                    UNION ALL
                    SELECT *, 3 as ordem_tipo FROM filtered_videos
                    WHERE @Ordem IS NULL OR @Ordem NOT IN ('newest', 'oldest', 'longest', 'shortest')
                )";

                // Consulta de contagem
                var consultaTotal = "SELECT COUNT(*) FROM filtered_videos";

                // Consulta principal com ordenação
                var consultaPrincipal = @"
                SELECT * FROM ordered_videos 
                ORDER BY 
                    ordem_tipo,
                    CASE WHEN @Ordem = 'newest' THEN data_adicionada END DESC NULLS LAST,
                    CASE WHEN @Ordem = 'oldest' THEN data_adicionada END ASC NULLS LAST,
                    CASE WHEN @Ordem = 'longest' THEN duracao_segundos END DESC NULLS LAST,
                    CASE WHEN @Ordem = 'shortest' THEN duracao_segundos END ASC NULLS LAST,
                    data_adicionada DESC
                OFFSET @Offset LIMIT @TamanhoPagina";

                // Executa contagem
                using var cmdTotal = new NpgsqlCommand(consultaBase + consultaTotal, conexao);
                ConfigurarParametros(cmdTotal, b, duracao, periodo, ordem, categoriaId, 0, 0);
                var total = Convert.ToInt32(await cmdTotal.ExecuteScalarAsync());

                // Executa consulta principal
                using var cmdVideos = new NpgsqlCommand(consultaBase + consultaPrincipal, conexao);
                ConfigurarParametros(cmdVideos, b, duracao, periodo, ordem, categoriaId, pagina, tamanhoPagina);

                var videos = new List<VideoBase>();
                using var reader = await cmdVideos.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    videos.Add(MapearVideo(reader));
                }

                var resultado = new ResultadoPaginado<VideoBase>
                {
                    Itens = videos,
                    PaginaAtual = pagina,
                    TamanhoPagina = tamanhoPagina,
                    TotalItens = total,
                    TotalPaginas = (int)Math.Ceiling(total / (double)tamanhoPagina)
                };

                await ArmazenarEmCache(chaveCache, resultado);
                return resultado;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter vídeos com filtros");
            throw;
        }
    }

    private void ConfigurarParametros(NpgsqlCommand cmd, string busca, string duracao, string periodo,
        string ordem, int categoriaId, int pagina, int tamanhoPagina)
    {
        cmd.Parameters.AddWithValue("@Busca", busca ?? "");
        cmd.Parameters.AddWithValue("@DuracaoFiltro", duracao ?? "");
        cmd.Parameters.AddWithValue("@PeriodoFiltro", periodo ?? "");
        cmd.Parameters.AddWithValue("@Ordem", ordem ?? "newest");
        cmd.Parameters.AddWithValue("@CategoriaId", categoriaId);

        if (pagina > 0)
        {
            cmd.Parameters.AddWithValue("@Offset", (pagina - 1) * tamanhoPagina);
            cmd.Parameters.AddWithValue("@TamanhoPagina", tamanhoPagina);
        }
    }


    public async Task<IEnumerable<(string Id, DateTime DataAdicionada)>> ObterVideosSitemapAsync(int offset, int limit)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync();

        var query = @"
        SELECT id, data_adicionada 
        FROM dev.videos 
        ORDER BY data_adicionada DESC 
        OFFSET @Offset LIMIT @Limit";

        using var cmd = new NpgsqlCommand(query, conexao);
        cmd.Parameters.AddWithValue("@Offset", offset);
        cmd.Parameters.AddWithValue("@Limit", limit);

        var resultados = new List<(string Id, DateTime DataAdicionada)>();
    
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            resultados.Add((
                reader.GetString(0),
                reader.GetDateTime(1)
            ));
        }

        return resultados;
    }
    public async Task<List<Categoria>> ObterCategoria()
    {
        try
        {
            var chaveCache = "categorias";
            var dadosCache = await _cache.GetAsync(chaveCache);

            if (dadosCache != null)
            {
                var dadosDescomprimidos = _usarCompressao
                    ? DescomprimirDados(dadosCache)
                    : dadosCache;

                return JsonSerializer.Deserialize<List<Categoria>>(
                    Encoding.UTF8.GetString(dadosDescomprimidos));
            }

            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                var categorias = new List<Categoria>();

                using var conexao = new NpgsqlConnection(_stringConexao);
                await conexao.OpenAsync();

                var consulta = "SELECT id, nome, mostrar FROM dev.categorias";
                using var comando = new NpgsqlCommand(consulta, conexao);

                using var leitor = await comando.ExecuteReaderAsync();
                while (await leitor.ReadAsync())
                {
                    categorias.Add(new Categoria
                    {
                        Id = leitor.GetInt32(0),
                        Nome = leitor.GetString(1),
                        Mostrar = !leitor.IsDBNull(2) ? leitor.GetBoolean(2) : false
                    });
                }

                await ArmazenarEmCache(chaveCache, categorias);
                return categorias;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter categorias");
            throw;
        }
    }

    #region "--=[Métodos para deixar dados em cache]=--"
    
    private async Task ArmazenarEmCache<T>(string chave, T dados)
    {
        var jsonDados = JsonSerializer.Serialize(dados);
        byte[] dadosComprimidos = _usarCompressao
            ? ComprimirDados(Encoding.UTF8.GetBytes(jsonDados))
            : Encoding.UTF8.GetBytes(jsonDados);

        var opcoesCache = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_tempoExpiracaoMinutos),
            SlidingExpiration = TimeSpan.FromMinutes(_tempoSlidingMinutos)
        };

        await _cache.SetAsync(chave, dadosComprimidos, opcoesCache);

        // Armazena em memória
        _memoryCache?.Set(chave, dados, TimeSpan.FromSeconds(60));
    }

    private byte[] ComprimirDados(byte[] dados)
    {
        using var outputStream = new MemoryStream();
        using (var gzip = new GZipStream(outputStream, CompressionMode.Compress))
        {
            gzip.Write(dados, 0, dados.Length);
        }

        return outputStream.ToArray();
    }

    private byte[] DescomprimirDados(byte[] dadosComprimidos)
    {
        using var inputStream = new MemoryStream(dadosComprimidos);
        using var outputStream = new MemoryStream();
        using (var gzip = new GZipStream(inputStream, CompressionMode.Decompress))
        {
            gzip.CopyTo(outputStream);
        }

        return outputStream.ToArray();
    }

    #endregion


    public async Task<ResultadoPaginado<VideoBase>> ObterVideosAsync(int pagina = 1, int tamanhoPagina = 36)
    {
        string chaveCache = $"videos_pagina_{pagina}_{tamanhoPagina}";

        try
        {
            if (_memoryCache != null)
            {
                if (_memoryCache.TryGetValue<ResultadoPaginado<VideoBase>>(chaveCache, out var resultadoMemoria))
                {
                    _logger.LogInformation("Dados obtidos do cache em memória para página {Pagina}", pagina);
                    return resultadoMemoria;
                }
            }
            
            var dadosCache = await _cache.GetAsync(chaveCache);
            if (dadosCache != null)
            {
                var dadosDescomprimidos = _usarCompressao
                    ? DescomprimirDados(dadosCache)
                    : dadosCache;
            
                var resultado = JsonSerializer.Deserialize<ResultadoPaginado<VideoBase>>(
                    Encoding.UTF8.GetString(dadosDescomprimidos));
            
                // Guarda em memória se possível
                _memoryCache?.Set(chaveCache, resultado, TimeSpan.FromSeconds(30));
            
                return resultado;
            }

            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                var resultado = await ObterVideosPaginadosDoBanco(pagina, tamanhoPagina);
                await ArmazenarEmCache(chaveCache, resultado);

                // Guarda em memória se possível
                _memoryCache?.Set(chaveCache, resultado, TimeSpan.FromSeconds(30));

                if (_onPaginaCacheada != null)
                {
                    await _onPaginaCacheada(resultado);
                }

                return resultado;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter vídeos da página {Pagina}", pagina);
            throw;
        }
    }

    //Listagem normal
    private async Task<ResultadoPaginado<VideoBase>> ObterVideosPaginadosDoBanco(int pagina, int tamanhoPagina)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync();

        var offset = (pagina - 1) * tamanhoPagina;

        // Consulta para obter o total de registros
        var consultaTotal = "SELECT COUNT(*) FROM dev.videos_com_miniaturas_normal";
        using var comandoTotal = new NpgsqlCommand(consultaTotal, conexao);
        var total = Convert.ToInt32(await comandoTotal.ExecuteScalarAsync());

        var consultaVideos = @"select * from dev.videos_com_miniaturas_normal 
                                ORDER BY data_adicionada DESC
                                    OFFSET @Offset LIMIT @TamanhoPagina";

        var videos = new List<VideoBase>();
        using (var comando = new NpgsqlCommand(consultaVideos, conexao))
        {
            comando.Parameters.AddWithValue("@Offset", offset);
            comando.Parameters.AddWithValue("@TamanhoPagina", tamanhoPagina);

            try
            {
                using var leitor = await comando.ExecuteReaderAsync();
                while (await leitor.ReadAsync())
                {
                    var miniaturasJson = leitor["miniaturas"] as string;
                    var miniaturas = string.IsNullOrEmpty(miniaturasJson)
                        ? new List<Miniaturas>()
                        : JsonConvert.DeserializeObject<List<Miniaturas>>(miniaturasJson);

                    videos.Add(new VideoBase
                    {
                        Id = leitor.GetString(0),
                        Titulo = leitor.GetString(1),
                        DuracaoSegundos = leitor.GetInt32(2),
                        Embed = leitor.GetString(3),
                        DefaultThumbSize = leitor.GetString(4),
                        DefaultThumbSrc = leitor.GetString(5),
                        DuracaoMinutos = leitor.GetString(6),
                        Miniaturas = miniaturas
                    });
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        return new ResultadoPaginado<VideoBase>
        {
            Itens = videos,
            PaginaAtual = pagina,
            TamanhoPagina = tamanhoPagina,
            TotalItens = total,
            TotalPaginas = (int)Math.Ceiling(total / (double)tamanhoPagina)
        };
    }



    //Busca por termo
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosPorTermoAsync(string termo, int pagina = 1, int tamanhoPagina = 36)
    {
        string chaveCache = $"videos_pagina_{termo}{pagina}_{tamanhoPagina}";

        try
        {
            // Tenta obter do cache
            var dadosCache = await _cache.GetAsync(chaveCache);
            if (dadosCache != null)
            {
                var dadosDescomprimidos = _usarCompressao
                    ? DescomprimirDados(dadosCache)
                    : dadosCache;

                return JsonSerializer.Deserialize<ResultadoPaginado<VideoBase>>(
                    Encoding.UTF8.GetString(dadosDescomprimidos));
            }

            // Se não estiver em cache, busca do banco usando circuit breaker
            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                //var resultado = await ObterVideosPorTermoPaginadosDoBanco(termo, pagina, tamanhoPagina);
                var resultado = await BuscarVideosAsync(termo, pagina, tamanhoPagina);
                await ArmazenarEmCache(chaveCache, resultado);
                return resultado;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter vídeos");
            throw;
        }
    }

    //Buscar videos por Titulo
    public async Task<ResultadoPaginado<VideoBase>> BuscarVideosAsync(
        string busca, 
        int pagina = 1, 
        int tamanhoPagina = 20, 
        CancellationToken cancellationToken = default)
    {
        
        busca = string.Join(" ", 
            busca.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrEmpty(t)));
        
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(cancellationToken);

        // Obtém o total de registros
        await using var cmdTotal = new NpgsqlCommand(Queries.BuscaVideos.ObterTotal, conexao);
        cmdTotal.Parameters.AddWithValue("@Busca", busca.ToLower());
        var total = Convert.ToInt32(await cmdTotal.ExecuteScalarAsync(cancellationToken));

        var offset = (pagina - 1) * tamanhoPagina;
        
        // Obtém os vídeos paginados
        await using var cmdVideos = new NpgsqlCommand(Queries.BuscaVideos.ObterVideos, conexao);
        cmdVideos.Parameters.AddWithValue("@Busca", busca.ToLower());
        cmdVideos.Parameters.AddWithValue("@Limite", tamanhoPagina);
        cmdVideos.Parameters.AddWithValue("@Offset", offset);

        await using var reader = await cmdVideos.ExecuteReaderAsync(cancellationToken);
        var videos = new List<VideoBase>();

        while (await reader.ReadAsync(cancellationToken))
        {
            videos.Add(MapearVideo(reader));
        }

        return new ResultadoPaginado<VideoBase>
        {
            Itens = videos,
            PaginaAtual = pagina,
            TamanhoPagina = tamanhoPagina,
            TotalItens = total,
            TotalPaginas = (int)Math.Ceiling(total / (double)tamanhoPagina)
        };
    }

    private static VideoBase MapearVideo(NpgsqlDataReader reader)
    {
        var miniaturasJson = reader["miniaturas"] as string;
        var miniaturas = string.IsNullOrEmpty(miniaturasJson)
            ? new List<Miniaturas>()
            : JsonConvert.DeserializeObject<List<Miniaturas>>(miniaturasJson);

        return new VideoBase
        {
            Id = reader.GetString(0),
            Titulo = reader.GetString(1),
            DuracaoSegundos = reader.GetInt32(2),
            Embed = reader.GetString(3),
            DefaultThumbSize = reader.GetString(4),
            DefaultThumbSrc = reader.GetString(5),
            DuracaoMinutos = reader.GetString(6),
            Miniaturas = miniaturas
        };
    }
    

    //Listagem por categoria
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosPorCategoriaAsync(int categoriaId, int pagina = 1, int tamanhoPagina = 36)
    {
        string chaveCache = $"videos_categoria_{categoriaId}{pagina}_{tamanhoPagina}";

        try
        {
            // Tenta obter do cache
            var dadosCache = await _cache.GetAsync(chaveCache);
            if (dadosCache != null)
            {
                var dadosDescomprimidos = _usarCompressao
                    ? DescomprimirDados(dadosCache)
                    : dadosCache;

                return JsonSerializer.Deserialize<ResultadoPaginado<VideoBase>>(
                    Encoding.UTF8.GetString(dadosDescomprimidos));
            }

            // Se não estiver em cache, busca do banco usando circuit breaker
            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                var resultado = await ObterVideosPorCategoriaPaginadosDoBanco(categoriaId, pagina, tamanhoPagina);
                await ArmazenarEmCache(chaveCache, resultado);
                return resultado;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter vídeos");
            throw;
        }
    }

    private async Task<ResultadoPaginado<VideoBase>> ObterVideosPorCategoriaPaginadosDoBanco(int categoriaId, int pagina, int tamanhoPagina)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync();

        var offset = (pagina - 1) * tamanhoPagina;

        // Consulta para obter o total de registros
        var consultaTotal = @"
            WITH unique_videos AS (
                SELECT DISTINCT vi.id
                FROM dev.videos_com_miniaturas_normal vi
                INNER JOIN dev.video_categorias vc ON vi.id = vc.video_id
                WHERE vc.categoria_id = @CategoriaId
            )
            SELECT count(vi.*)
            FROM dev.videos_com_miniaturas_normal vi
            INNER JOIN unique_videos uv ON vi.id = uv.id";

        using var comandoTotal = new NpgsqlCommand(consultaTotal, conexao);
        comandoTotal.Parameters.AddWithValue("@CategoriaId", categoriaId);
        var total = Convert.ToInt32(await comandoTotal.ExecuteScalarAsync());

        // Consulta paginada com miniaturas agregadas
        var consultaVideos = @"
                WITH unique_videos AS (
                    SELECT DISTINCT vi.id
                    FROM dev.videos_com_miniaturas_normal vi
                    INNER JOIN dev.video_categorias vc ON vi.id = vc.video_id
                    WHERE vc.categoria_id = @CategoriaId
                )
                SELECT 
                    * from dev.videos_com_miniaturas_normal vi
                INNER JOIN unique_videos uv ON vi.id = uv.id
                ORDER BY vi.data_adicionada DESC
                OFFSET @Offset LIMIT @TamanhoPagina";

        var videos = new List<VideoBase>();
        using (var comando = new NpgsqlCommand(consultaVideos, conexao))
        {
            comando.Parameters.AddWithValue("@CategoriaId", categoriaId);
            comando.Parameters.AddWithValue("@Offset", offset);
            comando.Parameters.AddWithValue("@TamanhoPagina", tamanhoPagina);

            using var leitor = await comando.ExecuteReaderAsync();
            while (await leitor.ReadAsync())
            {
                var miniaturasJson = leitor["miniaturas"] as string;
                var miniaturas = string.IsNullOrEmpty(miniaturasJson)
                    ? new List<Miniaturas>()
                    : JsonConvert.DeserializeObject<List<Miniaturas>>(miniaturasJson);

                videos.Add(new VideoBase
                {
                    Id = leitor.GetString(0),
                    Titulo = leitor.GetString(1),
                    DuracaoSegundos = leitor.GetInt32(2),
                    Embed = leitor.GetString(3),
                    DefaultThumbSize = leitor.GetString(4),
                    DefaultThumbSrc = leitor.GetString(5),
                    DuracaoMinutos = leitor.GetString(6),
                    Miniaturas = miniaturas
                });
            }
        }

        return new ResultadoPaginado<VideoBase>
        {
            Itens = videos,
            PaginaAtual = pagina,
            TamanhoPagina = tamanhoPagina,
            TotalItens = total,
            TotalPaginas = (int)Math.Ceiling(total / (double)tamanhoPagina)
        };
    }

    

    
    //V
    public async Task<VideoBase> ObterVideoPorIdAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            string chaveCache = $"video_{id}";
            var dadosCache = await _cache.GetAsync(chaveCache);
            if (dadosCache != null)
            {
                var dadosDescomprimidos = _usarCompressao
                    ? DescomprimirDados(dadosCache)
                    : dadosCache;

                return JsonSerializer.Deserialize<VideoBase>(
                    Encoding.UTF8.GetString(dadosDescomprimidos));
            }

            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                // Obtém dados básicos do vídeo primeiro
                VideoBase video;
                using (var conexao = new NpgsqlConnection(_stringConexao))
                {
                    await conexao.OpenAsync(cancellationToken);
                    video = await ObterDadosBasicosVideoAsync(conexao, id, cancellationToken);
                }

                if (video == null) return null;

                video.Termos = await ObterTermosComNovaConexaoAsync(id, cancellationToken);

                try
                {
                    // Agora obtém os vídeos relacionados usando os termos já carregados
                    await using (var conexao = new NpgsqlConnection(_stringConexao))
                    {
                        await conexao.OpenAsync(cancellationToken);
                        video.VideosRelacionados = await ObterVideosRelacionadosAsync(conexao, id, video.Termos, cancellationToken); 
                     
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }

                await ArmazenarEmCache($"video_{id}", video);
                return video;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao obter vídeo por ID: {VideoId}", id);
            throw;
        }
    }
    private async Task<List<Termos>> ObterTermosComNovaConexaoAsync(string id, CancellationToken cancellationToken)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync(cancellationToken);
        return await ObterTermosAsync(conexao, id, cancellationToken);
    }
    private async Task<VideoBase> ObterDadosBasicosVideoAsync(NpgsqlConnection conexao, string id, CancellationToken cancellationToken)
    {
        using var cmd = new NpgsqlCommand(Queries.VideoBase, conexao);
        cmd.Parameters.AddWithValue("@Id", id);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new VideoBase
        {
            Id = reader.GetString(0),
            Titulo = reader.GetString(1),
            DuracaoSegundos = reader.GetInt32(2),
            Embed = reader.GetString(3),
            DefaultThumbSize = reader.GetString(4),
            DefaultThumbSrc = reader.GetString(5),
            DuracaoMinutos = reader.GetString(6)
        };
    }
    private async Task<List<Termos>> ObterTermosAsync(NpgsqlConnection conexao, string id, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(Queries.Termos, conexao);
        cmd.Parameters.AddWithValue("@Id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var termos = new List<Termos>();

        while (await reader.ReadAsync(cancellationToken))
        {
            termos.Add(new Termos
            {
                Id = reader.GetInt32(0),
                termo = reader.GetString(1)
            });
        }

        return termos;
    }
    private async Task<List<VideoBase>> ObterVideosRelacionadosAsync(NpgsqlConnection conexao, string id, List<Termos> termos, CancellationToken cancellationToken)
    {
        if (!termos.Any())
            return new List<VideoBase>();

        string query = @"SELECT vi.*
                    FROM videos.dev.videos_com_miniaturas_normal vi
                    INNER JOIN (
                        SELECT DISTINCT v.id
                        FROM videos.dev.video_termos vt
                        INNER JOIN videos.dev.videos_com_miniaturas_normal v ON v.id = vt.video_id
                        INNER JOIN videos.dev.video_termos vt2 ON vt2.video_id = v.id
                        WHERE vt2.termo_id = @TermoId AND v.id != @Id
                        LIMIT 12
                    ) vr ON vr.id = vi.id
                    ORDER BY vi.data_adicionada DESC";
        
        // 1. Obter vídeos relacionados usando os termos já carregados
        var videosRelacionados = new List<VideoBase>();
        using (var cmd = new NpgsqlCommand(query, conexao))
        {
            var termoIds = termos.Select(t => t.Id).ToArray();
            cmd.Parameters.AddWithValue("@TermoId", termoIds.FirstOrDefault());
            cmd.Parameters.AddWithValue("@Id", id);

            using var leitor = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await leitor.ReadAsync(cancellationToken))
            {
                var miniaturasJson = leitor["miniaturas"] as string;
                var miniaturas = string.IsNullOrEmpty(miniaturasJson)
                    ? new List<Miniaturas>()
                    : JsonConvert.DeserializeObject<List<Miniaturas>>(miniaturasJson);
                
                videosRelacionados.Add(new VideoBase
                {
                    Id = leitor.GetString(0),
                    Titulo = leitor.GetString(1),
                    DuracaoSegundos = leitor.GetInt32(2),
                    Embed = leitor.GetString(3),
                    DefaultThumbSize = leitor.GetString(4),
                    DefaultThumbSrc = leitor.GetString(5),
                    DuracaoMinutos = leitor.GetString(6),
                    Miniaturas = miniaturas
                });
            }


        }

        return videosRelacionados;
    }

    private static class Queries
    {
        public const string VideoBase = @"
            SELECT 
                id, titulo, duracao_segundos, embed,
                default_thumb_size, default_thumb_src, duracao_minutos
            FROM videos.dev.videos 
            WHERE id = @Id";

        public const string Termos = @"
            SELECT DISTINCT
                te.id,
                te.termo
            FROM videos.dev.video_termos vt 
            INNER JOIN videos.dev.termos te ON vt.termo_id = te.id
            WHERE vt.video_id = @Id";

        public static class BuscaVideos
        {
            public const string ObterTotal = @"
            WITH termos_busca AS (
                SELECT unnest(string_to_array(LOWER(@Busca), ' ')) as termo
            )
            SELECT COUNT(DISTINCT v.id)
            FROM videos.dev.videos_com_miniaturas_normal v
            WHERE  EXISTS (
                SELECT 1 FROM termos_busca tb
                WHERE LOWER(v.titulo) LIKE '%' || tb.termo || '%'
            )";

            public const string ObterVideos = @"
            WITH termos_busca AS (
                SELECT unnest(string_to_array(LOWER(@Busca), ' ')) as termo
            ),
            videos_matches AS (
                SELECT 
                    v.id,
                    COUNT(DISTINCT tb.termo) as matches_count
                FROM videos.dev.videos_com_miniaturas_normal v
                CROSS JOIN termos_busca tb
                WHERE LOWER(v.titulo) LIKE '%' || tb.termo || '%'
                GROUP BY v.id
            )
            SELECT v.*
            FROM videos.dev.videos_com_miniaturas_normal v
            INNER JOIN videos_matches vm ON v.id = vm.id
            ORDER BY 
                vm.matches_count DESC,
                v.data_adicionada DESC
            LIMIT @Limite OFFSET @Offset";
        }
    }
}

