using paraiso.tradutor;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CategoryNameTranslator>();

var host = builder.Build();
host.Run();