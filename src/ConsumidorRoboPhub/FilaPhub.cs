using System.Text;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using paraiso.models;
using paraiso.models.PHub;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RoboPhub.Mapper;

namespace ConsumidorRoboPhub;

public class RabbitMqConsumer
{
    private readonly string _hostName = "209.126.11.117";
    private readonly string _userName = "dbotdev";
    private readonly string _password = "N3tP@ssW0rd!2023";
    private readonly string _exchangeName = "paraiso-phub";
    private readonly string _queueName = "paraiso-phub-searchvideos";

    private readonly string _connectionString = "Host=109.199.118.135;Username=dbotprod;Password=P4r41s0Pr01b1d0;Database=videos";
    Dictionary<int, string> termosExistentes = new();
    
    public void IniciarConsumo()
    {
        var factory = new ConnectionFactory()
        {
            HostName = _hostName,
            UserName = _userName,
            Password = _password
        };

        var connection = factory.CreateConnection();
        var channel = connection.CreateModel();

        // Declara a exchange e a fila e faz o bind entre elas
        channel.ExchangeDeclare(exchange: _exchangeName, type: ExchangeType.Direct, durable: true);
        channel.QueueDeclare(queue: _queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
        channel.QueueBind(queue: _queueName, exchange: _exchangeName, routingKey: "");

        var consumer = new EventingBasicConsumer(channel);
        
        consumer.Received += (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            
            // Aqui você deserializa e processa o objeto VideosHome
            var videosHome = JsonConvert.DeserializeObject<VideosHome>(message);

            ColocarVideosNoBanco(videosHome).Wait();
            
            // Confirma que a mensagem foi processada com sucesso
            channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
            GC.Collect();
        };
        
        channel.BasicQos(0, 10, false);
        // Inicia o consumo da fila
        channel.BasicConsume(queue: _queueName, autoAck: false, consumer: consumer);

    }
    private async Task<List<Termo>> CarregarTermosExistentes(NpgsqlConnection connection)
    {
        return (await connection.QueryAsync<Termo>("SELECT id, termo FROM dev.termos")).ToList();
    }
    private async Task ColocarVideosNoBanco(VideosHome videosHome)
    {
        Console.WriteLine($"Mensagem recebida: {DateTime.Now}");
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        var termos = await CarregarTermosExistentes(connection);
        termosExistentes = termos.ToDictionary(t => t.Id, t => t.termo);

        foreach (var video in videosHome.Videos)
        {
            CadastrarVideo(connection, video.Video.MapperToModel());
            
            Console.WriteLine($"Video gravado: {DateTime.Now}");

            CadastrarThumbs(connection,
                video.Video.Thumbs.Select(t =>
                    new Miniatura(video.Video.VideoId, t.Size, t.Width, t.Height, t.Src,
                        t.Size == video.Video.DefaultThumb)).ToList());

            Console.WriteLine($"Cadastrar Thumbs: {DateTime.Now}");

            CadastrarTermos(connection, video.Video.Tags, termosExistentes, video.Video.VideoId);

            Console.WriteLine($"Cadastrar Termos: {DateTime.Now}");

        }
    }
    private void CadastrarTermos(NpgsqlConnection connection, List<Tags> termos, Dictionary<int, string> termosExistentes, string videoId)
    {
        foreach (var termo in termos)
        {
            var termoIndex = termosExistentes.Values.Contains(termo.TagName) ? 
                termosExistentes.FirstOrDefault(x => x.Value == termo.TagName).Key : 0;

            int idTermo = 0;
            
            if (termoIndex == 0)
            {
                idTermo = connection.QuerySingle<int>(@"INSERT INTO dev.termos (termo) VALUES (@Termo) RETURNING id", new { Termo = termo.TagName });
                termosExistentes.Add(idTermo, termo.TagName);
            }
            else
            {
                var tEncontrado = termosExistentes[termoIndex];
                idTermo = termosExistentes.FirstOrDefault(x => x.Value == tEncontrado).Key;

                if (!this.termosExistentes.ContainsValue(termo.TagName))
                {
                    termosExistentes.Add(idTermo, termo.TagName);
                }
            }
            
            CadastrarVideoTermos(connection, videoId, idTermo);
        }
    }
    private void CadastrarVideoTermos(NpgsqlConnection connection, string videoId, int termoId)
    {
        var sql = @"INSERT INTO dev.video_termos (video_id, termo_id) VALUES (@VideoId, @TermoId)";
        connection.Execute(sql, new { VideoId = videoId, TermoId = termoId });
    }
    private void CadastrarVideo(NpgsqlConnection connection, paraiso.models.Video video)
    {
        try
        {
            // 1. Verifica se o vídeo já existe
            var sqlSelect = "SELECT COUNT(1) FROM dev.videos WHERE id = @Id";
            var videoExiste = connection.ExecuteScalar<int>(sqlSelect, new { video.Id }) > 0;

            if (videoExiste)
            {
                // 2. Se existir, faz o update
                var sqlUpdate = @"UPDATE dev.videos
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
                connection.Execute(sqlUpdate, video);

                connection.Execute("DELETE FROM dev.video_termos WHERE video_id = @VideoId",
                    new { VideoId = video.Id });
                connection.Execute("DELETE FROM dev.miniaturas WHERE video_id = @VideoId", new { VideoId = video.Id });
            }
            else
            {
                // 3. Se não existir, faz o insert
                var sqlInsert = @"INSERT INTO dev.videos 
                                (id, titulo, visualizacoes, avaliacao, url, data_adicionada, duracao_segundos, duracao_minutos, embed, site_id, default_thumb_size, default_thumb_width, default_thumb_height, default_thumb_src)
                              VALUES 
                                (@Id, @Titulo, @Visualizacoes, @Avaliacao, @Url, @DataAdicionada, @DuracaoSegundos, @DuracaoMinutos, @Embed, @SiteId, @DefaultThumbSize, @DefaultThumbWidth, @DefaultThumbHeight, @DefaultThumbSrc)";
                connection.Execute(sqlInsert, video);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }
    private void CadastrarThumbs(NpgsqlConnection connection, List<Miniatura> thumbs)
    {
        var sql = @"INSERT INTO dev.miniaturas (video_id, tamanho, largura, altura, src, padrao)
                    VALUES (@VideoId, @Tamanho, @Largura, @Altura, @Src, @Padrao)";
        connection.Execute(sql, thumbs);
    }
}