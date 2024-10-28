using System.Net.Http.Json;
using System.Text;
using Dapper;
using Newtonsoft.Json;
using Npgsql;
using paraiso.models;
using paraiso.models.PHub;
using RabbitMQ.Client;
using RoboPhub.Mapper;
using Video = paraiso.models.PHub.Video;

namespace RoboPhub;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly string _connectionString = "Host=109.199.118.135;Username=dbotprod;Password=P4r41s0Pr01b1d0;Database=videos";
    private readonly HttpClient _httpClient = new();
    Dictionary<int, string> termosExistentes = new();
    
    private readonly string _hostName = "209.126.11.117";
    private readonly string _userName = "dbotdev";
    private readonly string _password = "N3tP@ssW0rd!2023";
    private readonly string _exchangeName = "paraiso-phub";
    private readonly string _queueName = "paraiso-phub-searchvideos";
    private IModel channel;
    
    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;


        var factory = new ConnectionFactory()
        {
            HostName = _hostName,
            UserName = _userName,
            Password = _password
        };

        using var connection = factory.CreateConnection();
        channel = connection.CreateModel();

        // Declara a exchange e a fila e faz o bind entre elas
        channel.ExchangeDeclare(exchange: _exchangeName, type: ExchangeType.Direct, durable: true);
        
        channel.QueueDeclare(queue: _queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
        
        channel.QueueBind(queue: _queueName, exchange: _exchangeName, routingKey: "");

    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await IniciarBuscasNaAPI();
            await Task.Delay(1000, stoppingToken);
            Console.WriteLine("Finalizando o processo");
            break;
        }
    }
    
    private async Task IniciarBuscasNaAPI()
    {
        int i = 2000;
        while (true)
        {
            var resposta = await CarregarVideos(i);

            if (resposta == null)
            {
                break;
            }
            
            EnviarParaFila(resposta);
            Console.WriteLine("Registrou a página: " + i);
            
            i++;
        }
        Console.WriteLine("Última página recuperada: " + i);
    }
    private async Task<VideosHome?> CarregarVideos(int page = 1)
    {
        var videosResponse = await _httpClient
            .GetAsync("https://api.redtube.com/?data=redtube.Videos.searchVideos&output=json&thumbsize=all&page=" + page);
        
        if (videosResponse.IsSuccessStatusCode)
        {
            try
            {
                return await videosResponse.Content.ReadFromJsonAsync<VideosHome>();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        return null;
    }
    public void EnviarParaFila(VideosHome videosHome)
    {


        // Serializa o objeto VideosHome em JSON
        var message = JsonConvert.SerializeObject(videosHome);
        var body = Encoding.UTF8.GetBytes(message);

        // Envia a mensagem para a exchange, que direciona para a fila
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true; // Marca a mensagem como persistente

        channel.BasicPublish(exchange: _exchangeName,
            routingKey: "",
            basicProperties: properties,
            body: body);

        Console.WriteLine(" [x] Enviado para a fila: {0}", message);
    }
}