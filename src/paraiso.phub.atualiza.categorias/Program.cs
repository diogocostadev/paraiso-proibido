using paraiso.phub.atualiza.categorias;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CategoryWorker>();

var host = builder.Build();
host.Run();