using Npgsql;
using System.Data;
using NpgsqlTypes;

namespace paraiso.robo;

public class RoboPalavrasProibidas : BackgroundService
{
    private readonly ILogger<RoboPalavrasProibidas> _logger;
    private readonly string _connectionString;

    private bool _atualizaView = false;
    
    public RoboPalavrasProibidas(
        ILogger<RoboPalavrasProibidas> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("conexao-palavras-proibidas");
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessWordDeactivations(stoppingToken);

                try
                {
                    await RefreshMaterializedView(stoppingToken);
                    _atualizaView = false;
                }
                catch (Exception viewEx)
                {
                    // Log o erro mas não interrompe o processo principal
                    _logger.LogError(viewEx,
                        "Erro ao atualizar materialized view - continuando o processamento");
                }

                _logger.LogInformation("Processamento de palavras proibidas concluído");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar palavras proibidas");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Serviço de palavras proibidas sendo encerrado");
                break;
            }
        }
    }

    private async Task ProcessWordDeactivations(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        var palavras = await GetPalavrasAtivas(connection, stoppingToken);
        var totalProcessado = 0;

        foreach (var palavra in palavras)
        {
            await using var transaction = await connection.BeginTransactionAsync(stoppingToken);
            try
            {
                var processados = await ProcessarVideos(connection, palavra, transaction, stoppingToken);
                totalProcessado += processados;
                await transaction.CommitAsync(stoppingToken);

                _logger.LogInformation(
                    "Palavra '{Palavra}' processada: {Processados} vídeos inativados",
                    palavra.Palavra,
                    processados);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(stoppingToken);
                _logger.LogError(ex, "Erro ao processar palavra: {Palavra}", palavra.Palavra);
            }
        }

        _atualizaView = totalProcessado > 0;
        _logger.LogInformation("Total de vídeos processados: {Total}", totalProcessado);
    }

    private async Task<List<(int Id, string Palavra, string PalavraTroca)>> GetPalavrasAtivas(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var palavras = new List<(int Id, string Palavra, string PalavraTroca)>();

        const string sql = @"
            SELECT id, palavra, palavra_troca 
            FROM dev.palavras_substituicao 
            WHERE ativo = true 
            AND (ultima_execucao IS NULL 
                OR ultima_execucao < CURRENT_TIMESTAMP - interval '6 hours')";

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            palavras.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2)
            ));
        }

        return palavras;
    }

    private async Task<int> ProcessarVideos(
        NpgsqlConnection connection,
        (int Id, string Palavra, string PalavraTroca) palavra,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var substituicoes = 0;

        const string sqlSelect = @"
        SELECT CAST(id AS text), titulo 
        FROM dev.videos 
        WHERE ativo = true
        AND (
            lower(titulo) LIKE '%' || lower($1) || '%'
            OR lower(titulo) LIKE '%' || lower($2) || '%'
            OR lower(titulo) LIKE '%' || lower($3) || '%'
        )";

        await using var cmdSelect = new NpgsqlCommand(sqlSelect, connection, transaction);
        cmdSelect.Parameters.AddWithValue(palavra.Palavra);
        cmdSelect.Parameters.AddWithValue(palavra.Palavra.ToUpper());
        cmdSelect.Parameters.AddWithValue(char.ToUpper(palavra.Palavra[0]) + palavra.Palavra.Substring(1).ToLower());

        var videosParaInativar = new List<(int Id, string Titulo)>();

        await using (var reader = await cmdSelect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var idString = reader.GetString(0);
                if (int.TryParse(idString, out int id))
                {
                    var titulo = reader.GetString(1);
                    videosParaInativar.Add((id, titulo));
                }
                else
                {
                    _logger.LogWarning("ID inválido encontrado: {Id}", idString);
                }
            }
        }

        if (videosParaInativar.Any())
        {
            foreach (var video in videosParaInativar)
            {
                try
                {
                    // Registra na tabela de vídeos inativos
                    const string sqlInativo = @"
                    INSERT INTO dev.videos_inativos (video_id, motivo, data_inativacao) 
                    VALUES (CAST($1 AS INTEGER), $2, CURRENT_TIMESTAMP)
                    ON CONFLICT (video_id) DO NOTHING";

                    await using var cmdInativo = new NpgsqlCommand(sqlInativo, connection, transaction);
                    cmdInativo.Parameters.AddWithValue(video.Id.ToString());
                    cmdInativo.Parameters.AddWithValue($"Palavra proibida encontrada: {palavra.Palavra}");
                    await cmdInativo.ExecuteNonQueryAsync(cancellationToken);

                    // Inativa o vídeo
                    const string sqlUpdate = @"
                    UPDATE dev.videos 
                    SET ativo = false 
                    WHERE CAST(id AS INTEGER) = CAST($1 AS INTEGER)
                    AND ativo = true";

                    await using var cmdUpdate = new NpgsqlCommand(sqlUpdate, connection, transaction);
                    cmdUpdate.Parameters.AddWithValue(video.Id.ToString());
                    await cmdUpdate.ExecuteNonQueryAsync(cancellationToken);

                    // Registra a ocorrência
                    const string sqlOcorrencia = @"
                    INSERT INTO dev.palavras_ocorrencias 
                        (palavra_id, video_id, texto_anterior, texto_atual, tipo_alteracao) 
                    VALUES 
                        (CAST($1 AS INTEGER), CAST($2 AS INTEGER), $3, $4, 'video')";

                    await using var cmdOcorrencia = new NpgsqlCommand(sqlOcorrencia, connection, transaction);
                    cmdOcorrencia.Parameters.AddWithValue(palavra.Id.ToString());
                    cmdOcorrencia.Parameters.AddWithValue(video.Id.ToString());
                    cmdOcorrencia.Parameters.AddWithValue(video.Titulo);
                    cmdOcorrencia.Parameters.AddWithValue(video.Titulo);
                    await cmdOcorrencia.ExecuteNonQueryAsync(cancellationToken);

                    substituicoes++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Erro ao processar vídeo {VideoId} ({Titulo})",
                        video.Id, video.Titulo);
                    throw;
                }
            }
        }

        return substituicoes;
    }
}