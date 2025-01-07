using paraiso.robo;
using paraiso.robo.Eporner;
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
    DefaultDatabase = 1,
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

/*
// Robo: AtualizaCategorias
builder.Services.AddHostedService<RoboAtualizaCategorias>();

// Robo: AtualizaVideos
builder.Services.AddHostedService<RoboInsereVideosSeNaoExistir>();
*/

/******************************************/
// Robo: AtualizaCache
builder.Services.AddSingleton<ServiceAtualizaCache>(); //trabalham juntos
builder.Services.AddHostedService<RoboAtualizaCache>();

// Robo: Atualiza Videos por Palavras
builder.Services.AddHostedService<RoboPalavrasProibidas>();

// Robo: DeletedVideos
builder.Services.AddHostedService<RoboDeletaVideos>();

/** para quando for atualizar e rodar a base inteira, com insert e update de videos**/ 
//builder.Services.AddHostedService<RoboAtualizaBaseInteira2>();
//builder.Services.AddHostedService<RoboInsereVideosSeNaoExistirEpornerBaseInteira>();

var host = builder.Build();
host.Run();