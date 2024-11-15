using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;
using Npgsql;
using System.Text.Json;
using Polly;
using Polly.CircuitBreaker;
using System.IO.Compression;
using System.Text;
using paraiso.web.Models;

namespace paraiso.web.Services;

public class ServicoVideosCache
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<ServicoVideosCache> _logger;
    private readonly string _stringConexao;
    private readonly AsyncCircuitBreakerPolicy _circuitBreaker;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private Timer _cacheWarmingTimer;

    private bool _usarCompressao = true;
    private int _tempoExpiracaoMinutos = 5;
    private int _tempoSlidingMinutos = 1;
        
    public ServicoVideosCache(
        IDistributedCache cache,
        ILogger<ServicoVideosCache> logger,
        IConfiguration configuration,
        IHostApplicationLifetime applicationLifetime)
    {
        _cache = cache;
        _logger = logger;
        _stringConexao = "Host=212.56.47.25;Username=dbotprod;Password=P4r41s0Pr01b1d0;Database=videos";
        _applicationLifetime = applicationLifetime;

        // Configuração do Circuit Breaker
        _circuitBreaker = Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (ex, duration) =>
                {
                    _logger.LogWarning($"Circuit Breaker aberto por {duration.TotalSeconds} segundos devido a: {ex.Message}");
                },
                onReset: () =>
                {
                    _logger.LogInformation("Circuit Breaker resetado");
                });

        // Iniciar Cache Warming
        IniciarCacheWarming();
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
        
    public async Task<VideoBase> ObterVideoPorIdAsync(string id, int page)
    {
        try
        {
            string chaveCache = $"videos_pagina_{page}_100";
            var dadosCache = await _cache.GetAsync(chaveCache);

            if (dadosCache != null)
            {
                var dadosDescomprimidos = _usarCompressao
                    ? DescomprimirDados(dadosCache)
                    : dadosCache;

                var paginaCache = JsonSerializer.Deserialize<ResultadoPaginado<VideoBase>>(
                    Encoding.UTF8.GetString(dadosDescomprimidos));

                var videoEncontrado = paginaCache.Itens.FirstOrDefault(v => v.Id == id);
                if (videoEncontrado != null)
                {
                    return videoEncontrado;
                }
            }

                // Se não encontrou no cache, busca diretamente no banco
            return await _circuitBreaker.ExecuteAsync(async () =>
            {
                using var conexao = new NpgsqlConnection(_stringConexao);
                await conexao.OpenAsync();

                var consulta = @"
                SELECT id, titulo, duracao_segundos, embed, default_thumb_size, default_thumb_src 
                FROM dev.videos 
                WHERE id = @Id";

                using var comando = new NpgsqlCommand(consulta, conexao);
                comando.Parameters.AddWithValue("@Id", id);

                using var leitor = await comando.ExecuteReaderAsync();
                if (await leitor.ReadAsync())
                {
                    var video = new VideoBase
                    {
                        Id = leitor.GetString(0),
                        Titulo = leitor.GetString(1),
                        DuracaoSegundos = leitor.GetInt32(2),
                        Embed = leitor.GetString(3),
                        DefaultThumbSize = leitor.GetString(4),
                        DefaultThumbSrc = leitor.GetString(5)
                    };

                    // Armazena o resultado individual em cache
                    string chaveVideoIndividual = $"video_{id}";
                    await ArmazenarEmCache(chaveVideoIndividual, video);

                    return video;
                }

                return null; // Retorna null se não encontrar o vídeo
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao obter vídeo por ID: {id}");
            throw;
        }
    }

    #region "--=[Métodos para deixar dados em cache]=--"
    
    private void IniciarCacheWarming()
    {
        _cacheWarmingTimer = new Timer(
            async _ => await AquecerCache(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(4) // Executa a cada 4 minutos
        );
    }
    private async Task AquecerCache()
    {
        try
        {
            _logger.LogInformation("Iniciando aquecimento do cache...");
            
            // Pré-carrega as primeiras 5 páginas
            for (int i = 1; i <= 5; i++)
            {
                var videos = await ObterVideosAsync(i, 100);
                var chaveCache = $"videos_pagina_{i}_100";
                await ArmazenarEmCache(chaveCache, videos);
            }

            _logger.LogInformation("Aquecimento do cache concluído");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro durante o aquecimento do cache");
        }
    }
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
    
    //Listagem normal
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosAsync(int pagina = 1, int tamanhoPagina = 100)
    {
        string chaveCache = $"videos_pagina_{pagina}_{tamanhoPagina}";
        
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
                var resultado = await ObterVideosPaginadosDoBanco(pagina, tamanhoPagina);
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
    private async Task<ResultadoPaginado<VideoBase>> ObterVideosPaginadosDoBanco(int pagina, int tamanhoPagina)
    {
        using var conexao = new NpgsqlConnection(_stringConexao);
        await conexao.OpenAsync();

        var offset = (pagina - 1) * tamanhoPagina;
        
        // Consulta para obter o total de registros
        var consultaTotal = "SELECT COUNT(*) FROM dev.videos";
        using var comandoTotal = new NpgsqlCommand(consultaTotal, conexao);
        var total = Convert.ToInt32(await comandoTotal.ExecuteScalarAsync());

        // Consulta paginada
        var consultaVideos = @"
            select id, titulo, duracao_segundos, embed, default_thumb_size, default_thumb_src from videos.dev.videos
            where ativo = true
            ORDER BY data_adicionada DESC
            OFFSET @Offset LIMIT @TamanhoPagina";

        var videos = new List<VideoBase>();
        using (var comando = new NpgsqlCommand(consultaVideos, conexao))
        {
            comando.Parameters.AddWithValue("@Offset", offset);
            comando.Parameters.AddWithValue("@TamanhoPagina", tamanhoPagina);

            using var leitor = await comando.ExecuteReaderAsync();
            while (await leitor.ReadAsync())
            {
                videos.Add(new VideoBase
                {
                    Id = leitor.GetString(0),
                    Titulo = leitor.GetString(1),
                    DuracaoSegundos = leitor.GetInt32(2),
                    Embed = leitor.GetString(3),
                    DefaultThumbSize = leitor.GetString(4),
                    DefaultThumbSrc = leitor.GetString(5)
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

    //Busca por termo
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosPorTermoAsync(string termo, int pagina = 1, int tamanhoPagina = 100)
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
    private async Task<ResultadoPaginado<VideoBase>> ObterVideosPorTermoPaginadosDoBanco(string termo, int pagina, int tamanhoPagina)
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

        // Consulta paginada
        var consultaVideos = @"
            WITH unique_videos AS (
                SELECT DISTINCT vi.id
                FROM videos.dev.videos vi
                INNER JOIN videos.dev.video_termos vt ON vi.id = vt.video_id
                INNER JOIN videos.dev.termos te ON vt.termo_id = te.id
                WHERE te.termo LIKE @Termo
            )
            SELECT vi.id, vi.titulo, vi.duracao_segundos, vi.embed, vi.default_thumb_size, vi.default_thumb_src
            FROM videos.dev.videos vi
            INNER JOIN unique_videos uv ON vi.id = uv.id
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
                videos.Add(new VideoBase
                {
                    Id = leitor.GetString(0),
                    Titulo = leitor.GetString(1),
                    DuracaoSegundos = leitor.GetInt32(2),
                    Embed = leitor.GetString(3),
                    DefaultThumbSize = leitor.GetString(4),
                    DefaultThumbSrc = leitor.GetString(5)
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
    public async Task<ResultadoPaginado<VideoBase>> ObterVideosPorCategoriaAsync(int categoriaId, int pagina = 1, int tamanhoPagina = 100)
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

        // Consulta paginada
        var consultaVideos = @"
                WITH unique_videos AS (
                    SELECT DISTINCT vi.id
                    FROM videos.dev.videos vi
                    INNER JOIN dev.video_categorias vc ON vi.id = vc.video_id
                    WHERE vc.categoria_id = @CategoriaId
                )
                SELECT vi.id, vi.titulo, vi.duracao_segundos, vi.embed, vi.default_thumb_size, vi.default_thumb_src
                FROM dev.videos vi
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
                videos.Add(new VideoBase
                {
                    Id = leitor.GetString(0),
                    Titulo = leitor.GetString(1),
                    DuracaoSegundos = leitor.GetInt32(2),
                    Embed = leitor.GetString(3),
                    DefaultThumbSize = leitor.GetString(4),
                    DefaultThumbSrc = leitor.GetString(5)
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
}
