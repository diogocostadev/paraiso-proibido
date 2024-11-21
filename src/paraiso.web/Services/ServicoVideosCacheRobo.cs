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

public class ServicoVideosCacheRobo
{
    private readonly IMemoryCache _memoryCache;
    private readonly TimeSpan _memoryCacheDuration = TimeSpan.FromSeconds(30);

    private readonly IDistributedCache _cache;
    private readonly ILogger<ServicoVideosCacheRobo> _logger;
    private readonly string _stringConexao;
    private readonly AsyncCircuitBreakerPolicy _circuitBreaker;

    private bool _usarCompressao = true;
    private int _tempoExpiracaoMinutos = 10;
    private int _tempoSlidingMinutos = 1;

    public Func<ResultadoPaginado<VideoBase>, Task> _onPaginaCacheada;
    
    public ServicoVideosCacheRobo(IDistributedCache cache, ILogger<ServicoVideosCacheRobo> logger, IMemoryCache memoryCache)
    {
        _cache = cache;
        _memoryCache = memoryCache;
        
        _logger = logger;
        _stringConexao = 
            "Host=212.56.47.25;Username=dbotrobo;Password=P4r41s0Pr01b1d0;Database=videos;Maximum Pool Size=50;Timeout=30;Command Timeout=30;SSL Mode=Require;Trust Server Certificate=true;";

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


    public async Task<ResultadoPaginado<VideoBase>> ObterVideosAsync(int pagina = 1, int tamanhoPagina = 120)
    {
        string chaveCache = $"videos_pagina_{pagina}_{tamanhoPagina}";

        try
        {
            // Verifica se _memoryCache existe
            if (_memoryCache != null)
            {
                if (_memoryCache.TryGetValue<ResultadoPaginado<VideoBase>>(chaveCache, out var resultadoMemoria))
                {
                    _logger.LogInformation("Dados obtidos do cache em memória para página {Pagina}", pagina);
                    return resultadoMemoria;
                }
            }

            // Continua com o Redis e banco de dados...
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
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosPorTermoAsync(string termo, int pagina = 1,
        int tamanhoPagina = 120)
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
                var resultado = await ObterVideosPorTermoPaginadosDoBanco(termo, pagina, tamanhoPagina);
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

    private async Task<ResultadoPaginado<VideoBase>> ObterVideosPorTermoPaginadosDoBanco(string termo, int pagina,
        int tamanhoPagina)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync();

        var offset = (pagina - 1) * tamanhoPagina;

        // Consulta para obter o total de registros
        var consultaTotal = @"
        WITH unique_videos AS (
            SELECT DISTINCT vi.id
            FROM videos.dev.videos vi
            INNER JOIN videos.dev.video_termos vt ON vi.id = vt.video_id
            INNER JOIN videos.dev.termos te ON vt.termo_id = te.id
            WHERE te.termo LIKE @Termo
        )
        SELECT count(vi.*)
        FROM videos.dev.videos vi
        INNER JOIN unique_videos uv ON vi.id = uv.id";

        using var comandoTotal = new NpgsqlCommand(consultaTotal, conexao);
        comandoTotal.Parameters.AddWithValue("@Termo", $"%{termo}%");
        var total = Convert.ToInt32(await comandoTotal.ExecuteScalarAsync());

        // Consulta paginada com miniaturas agregadas
        var consultaVideos = @"
        WITH unique_videos AS (
            SELECT DISTINCT vi.id
            FROM videos.dev.videos vi
            INNER JOIN videos.dev.video_termos vt ON vi.id = vt.video_id
            INNER JOIN videos.dev.termos te ON vt.termo_id = te.id
            WHERE te.termo LIKE @Termo
        )
        SELECT 
            vi.id, 
            vi.titulo, 
            vi.duracao_segundos, 
            vi.embed, 
            vi.default_thumb_size, 
            vi.default_thumb_src, 
            vi.duracao_minutos AS DuracaoMinutos,
            jsonb_agg(
                jsonb_build_object(
                    'id', mi.id,
                    'tamanho', mi.tamanho,
                    'src', mi.src,
                    'altura', mi.altura,
                    'largura', mi.largura
                )
            ) AS miniaturas
        FROM 
            videos.dev.videos vi
        LEFT JOIN 
            videos.dev.miniaturas mi ON mi.video_id = vi.id AND mi.tamanho = vi.default_thumb_size
        INNER JOIN unique_videos uv ON vi.id = uv.id
        WHERE vi.ativo = true
        GROUP BY vi.id
        ORDER BY vi.data_adicionada DESC
        OFFSET @Offset LIMIT @TamanhoPagina";

        var videos = new List<VideoBase>();
        using (var comando = new NpgsqlCommand(consultaVideos, conexao))
        {
            comando.Parameters.AddWithValue("@Termo", $"%{termo}%");
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



    //Listagem por categoria
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosPorCategoriaAsync(int categoriaId, int pagina = 1,
        int tamanhoPagina = 120)
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

    private async Task<ResultadoPaginado<VideoBase>> ObterVideosPorCategoriaPaginadosDoBanco(int categoriaId,
        int pagina, int tamanhoPagina)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync();

        var offset = (pagina - 1) * tamanhoPagina;

        // Consulta para obter o total de registros
        var consultaTotal = @"
        WITH unique_videos AS (
            SELECT DISTINCT vi.id
            FROM dev.videos vi
            INNER JOIN dev.video_categorias vc ON vi.id = vc.video_id
            WHERE vc.categoria_id = @CategoriaId
        )
        SELECT count(vi.*)
        FROM dev.videos vi
        INNER JOIN unique_videos uv ON vi.id = uv.id";

        using var comandoTotal = new NpgsqlCommand(consultaTotal, conexao);
        comandoTotal.Parameters.AddWithValue("@CategoriaId", categoriaId);
        var total = Convert.ToInt32(await comandoTotal.ExecuteScalarAsync());

        // Consulta paginada com miniaturas agregadas
        var consultaVideos = @"
        WITH unique_videos AS (
            SELECT DISTINCT vi.id
            FROM videos.dev.videos vi
            INNER JOIN dev.video_categorias vc ON vi.id = vc.video_id
            WHERE vc.categoria_id = @CategoriaId
        )
        SELECT 
            vi.id, 
            vi.titulo, 
            vi.duracao_segundos, 
            vi.embed, 
            vi.default_thumb_size, 
            vi.default_thumb_src, 
            vi.duracao_minutos AS DuracaoMinutos,
            jsonb_agg(
                jsonb_build_object(
                    'id', mi.id,
                    'tamanho', mi.tamanho,
                    'src', mi.src,
                    'altura', mi.altura,
                    'largura', mi.largura
                )
            ) AS miniaturas
        FROM 
            dev.videos vi
        LEFT JOIN 
            dev.miniaturas mi ON mi.video_id = vi.id AND mi.tamanho = vi.default_thumb_size
        INNER JOIN unique_videos uv ON vi.id = uv.id
        WHERE vi.ativo = true
        GROUP BY vi.id
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

                // Executa as queries em paralelo
                video.Termos = await ObterTermosComNovaConexaoAsync(id, cancellationToken);

                try
                {
                    // Agora obtém os vídeos relacionados usando os termos já carregados
                    await using (var conexao = new NpgsqlConnection(_stringConexao))
                    {
                        await conexao.OpenAsync(cancellationToken);
                        video.VideosRelacionados = await ObterVideosRelacionadosAsync(conexao, id, video.Termos, cancellationToken); 
                     
                    }

                    await using (var conexao = new NpgsqlConnection(_stringConexao))
                    {
                        await conexao.OpenAsync(cancellationToken);
                        
                        foreach (var videosRela in video.VideosRelacionados)
                        {
                            videosRela.Miniaturas = await ObterMiniaturasParaVideoAsync(conexao, videosRela.Id, videosRela.DefaultThumbSize, cancellationToken);
                        }
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

        // 1. Obter vídeos relacionados usando os termos já carregados
        var videosRelacionados = new List<VideoBase>();
        using (var cmd = new NpgsqlCommand(Queries.VideosRelacionados.ObterVideosRelacionadosBasicos, conexao))
        {
            var termoIds = termos.Select(t => t.Id).ToArray();
            cmd.Parameters.AddWithValue("@TermoId", termoIds.FirstOrDefault());
            cmd.Parameters.AddWithValue("@Id", id);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                videosRelacionados.Add(new VideoBase
                {
                    Id = reader.GetString(0),
                    Titulo = reader.GetString(1),
                    DefaultThumbSrc = reader.GetString(2),
                    DefaultThumbSize = reader.GetString(3)
                    //Miniaturas = await ObterMiniaturasParaVideoAsync(conexao, reader.GetString(0), reader.GetString(3), cancellationToken)
                });
            }


        }

        return videosRelacionados;
    }
    private async Task<List<Miniaturas>> ObterMiniaturasParaVideoAsync(
        NpgsqlConnection conexao,
        string videoId,
        string defaultThumbSize,
        CancellationToken cancellationToken)
    {
        using var cmd = new NpgsqlCommand("select src from videos.dev.miniaturas where video_id = @VideoId and tamanho = @DefaultThumbSize", conexao);
        cmd.Parameters.AddWithValue("@VideoId", videoId);
        cmd.Parameters.AddWithValue("@DefaultThumbSize", defaultThumbSize);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var miniaturas = new List<Miniaturas>();

        while (await reader.ReadAsync(cancellationToken))
        {
            miniaturas.Add(new Miniaturas
            {
                Src = reader.GetString(0)
            });
        }

        return miniaturas;
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
        
        public static class VideosRelacionados
        {
            public const string ObterVideosRelacionadosBasicos = @"
                WITH videos_relacionados AS (
                SELECT 
                    v.id,
                    v.titulo,
                    v.default_thumb_src,
                    v.default_thumb_size,
                    COUNT(DISTINCT vt2.termo_id) AS termos_em_comum
                FROM 
                    videos.dev.video_termos vt
                INNER JOIN 
                    videos.dev.videos v ON v.id = vt.video_id
                INNER JOIN 
                    videos.dev.video_termos vt2 ON vt2.video_id = v.id
                WHERE 
                    vt2.termo_id = (@TermoId)
                    AND v.id != @Id
                GROUP BY 
                    v.id, v.titulo, v.default_thumb_src, v.default_thumb_size
            )
            SELECT 
                id,
                titulo,
                default_thumb_src,
                default_thumb_size,
                termos_em_comum  -- Inclua a coluna termos_em_comum no SELECT
            FROM 
                videos_relacionados
            ORDER BY 
                termos_em_comum DESC, id
            LIMIT 12";
        }
    }
}
