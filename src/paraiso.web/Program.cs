using Microsoft.Extensions.Caching.StackExchangeRedis;
using paraiso.web.Middleware;
using paraiso.web.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);


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

builder.Services.AddMemoryCache();
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<ServicoVideosCache>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Erro");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");


app.UseMiddleware<VerificaMais18>();

app.Run();