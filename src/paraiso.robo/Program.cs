using paraiso.robo;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Services
builder.Services.AddHttpClient();
var redisOptions = new ConfigurationOptions
{
    EndPoints = { "212.56.47.25:6379" },
    Password = "D1d4C0st4S4nt0s",
    ConnectTimeout = 5000,
    SyncTimeout = 5000,
    AsyncTimeout = 5000,
    ConnectRetry = 3,
    DefaultDatabase = 0,
    AbortOnConnectFail = false,
    AllowAdmin = true,
    ClientName = "CacheWarming",
    KeepAlive = 60,
    ReconnectRetryPolicy = new LinearRetry(1000)
};

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.ConfigurationOptions = redisOptions;
    options.InstanceName = "VideoCache_";
});

builder.Services.AddHostedService<RoboPalavrasProibidas>();



builder.Services.AddSingleton<ServiceAtualizaCache>();


// Robo: DeletedVideos
builder.Services.AddHostedService<RoboDeletaVideos>();

// Robo: AtualizaCategorias
builder.Services.AddHostedService<RoboAtualizaCategorias>();

// Robo: AtualizaVideos
builder.Services.AddHostedService<RoboInsereVideosSeNaoExistir>();

// Robo: AtualizaBaseInteira - julgo nao ser necessário esse robo por hora
// builder.Services.AddHostedService<RoboAtualizaBaseInteira>();

// Robo: AtualizaCache
builder.Services.AddHostedService<RoboAtualizaCache>();


var host = builder.Build();
host.Run();