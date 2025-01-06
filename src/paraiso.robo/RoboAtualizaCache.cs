
using paraiso.robo.ModelWeb;
using StackExchange.Redis;

namespace paraiso.robo;

public class RoboAtualizaCache : BackgroundService
{
    private readonly ILogger<RoboAtualizaCache> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ServiceAtualizaCache _cacheService;
    private readonly SemaphoreSlim _semaphore;

    // Configurações otimizadas
    private readonly int _intervaloHoras;
    private readonly int _maxPaginas;
    private readonly int _batchSize;
    private readonly int _maxConcurrentOperations;
    private readonly int _retryAttempts;
    private readonly TimeSpan _delayBetweenBatches;

    public RoboAtualizaCache(
        ILogger<RoboAtualizaCache> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ServiceAtualizaCache cacheService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _cacheService = cacheService;
        
        // Carrega configurações
        _intervaloHoras = 3;
        _maxPaginas = _configuration.GetValue<int>("CacheWarming:MaxPages", 10000);
        _batchSize = _configuration.GetValue<int>("CacheWarming:BatchSize", 5);
        _maxConcurrentOperations = _configuration.GetValue<int>("CacheWarming:MaxConcurrentOperations", 3);
        _retryAttempts = _configuration.GetValue<int>("CacheWarming:RetryAttempts", 3);
        _delayBetweenBatches = TimeSpan.FromSeconds(_configuration.GetValue<int>("CacheWarming:DelayBetweenBatchesSeconds", 10));
        
        _semaphore = new SemaphoreSlim(_maxConcurrentOperations);
        
        // Agora só registra evento se estiver configurado para processar vídeos individuais
        if (_configuration.GetValue<bool>("CacheWarming:ProcessIndividualVideos", false))
        {
            _cacheService._onPaginaCacheada += CarregarVideosEmCachePorPagina;
        }
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Iniciando ciclo de cache warming em: {time}", DateTimeOffset.Now);

            try
            {
                // Primeiro carrega categorias
                await ProcessWithRetry(() => ListarCategorias(_cacheService, stoppingToken));

                // Depois carrega apenas as páginas iniciais
                await ProcessInitialPages(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro durante o cache warming");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }

            await Task.Delay(TimeSpan.FromHours(_intervaloHoras), stoppingToken);
        }
    }
    
    private async Task ProcessInitialPages(CancellationToken stoppingToken)
    {
        for (int page = 1; page <= _maxPaginas; page += _batchSize)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var tasks = Enumerable.Range(page, Math.Min(_batchSize, _maxPaginas - page + 1))
                .Select(p => ProcessPageWithSemaphore(p, stoppingToken));

            try
            {
                await Task.WhenAll(tasks);
                _logger.LogInformation("Processado lote de páginas {StartPage} até {EndPage}", 
                    page, Math.Min(page + _batchSize - 1, _maxPaginas));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar lote iniciando na página {StartPage}", page);
            }

            await Task.Delay(_delayBetweenBatches, stoppingToken);
        }
    }

    private async Task ProcessPageWithSemaphore(int page, CancellationToken stoppingToken)
    {
        try
        {
            await _semaphore.WaitAsync(stoppingToken);
            await ProcessWithRetry(() => _cacheService.ObterVideosAsync(b:"", duracao: null, periodo: null, ordem: null, page, 36), stoppingToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ListarCategorias(ServiceAtualizaCache cacheService, CancellationToken stoppingToken)
    {
        try
        {
            await _semaphore.WaitAsync(stoppingToken);
            _logger.LogInformation("Iniciando cache warming de categorias");
            await cacheService.ObterCategoria();
            _logger.LogInformation("Cache warming de categorias concluído");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ProcessWithRetry(Func<Task> operation, CancellationToken stoppingToken = default)
    {
        var delay = TimeSpan.FromSeconds(2);

        for (int i = 0; i < _retryAttempts; i++)
        {
            try
            {
                await operation();
                return;
            }
            catch (RedisTimeoutException) when (i < _retryAttempts - 1)
            {
                _logger.LogWarning("Timeout no Redis, tentativa {Attempt} de {MaxAttempts}", i + 1, _retryAttempts);
                await Task.Delay(delay, stoppingToken);
                delay *= 2; // Exponential backoff
            }
            catch (Exception ex) when (i < _retryAttempts - 1)
            {
                _logger.LogError(ex, "Erro na tentativa {Attempt} de {MaxAttempts}", i + 1, _retryAttempts);
                await Task.Delay(delay, stoppingToken);
                delay *= 2;
            }
        }
    }

    private async Task CarregarVideosEmCachePorPagina(ResultadoPaginado<VideoBase> pagina)
    {
        try 
        {
            _logger.LogInformation("Cache warming de página concluído: {TotalItens} itens", pagina?.TotalItens ?? 0);

            const int batchSize = 5;
            for (var i = 0; i < pagina.Itens.Count; i += batchSize)
            {
                var tasks = pagina.Itens.Skip(i).Take(batchSize).Select(async video =>
                {
                    try
                    {
                        await _semaphore.WaitAsync();
                        await ProcessWithRetry(() => _cacheService.ObterVideoPorIdAsync(video.Id));
                    }
                    finally
                    {
                        _semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
                await Task.Delay(TimeSpan.FromSeconds(2)); // Delay entre lotes
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao carregar vídeos em cache da página");
        }
    }
    
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Serviço de cache warming está sendo encerrado");
        _semaphore.Dispose();
        await base.StopAsync(cancellationToken);
    }
}