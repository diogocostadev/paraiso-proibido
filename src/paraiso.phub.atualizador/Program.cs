using WorkerService1;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WorkerInserirSeNaoExistir>();

var host = builder.Build();
host.Run();