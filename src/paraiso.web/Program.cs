using paraiso.web.Middleware;
using paraiso.web.Services;

var builder = WebApplication.CreateBuilder(args);

// Adiciona o Distributed Cache (usando Redis, por exemplo)
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = "109.199.118.135:6379,abortConnect=false,ssl=false";
    options.InstanceName = "RedisCacheInstance";
});
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