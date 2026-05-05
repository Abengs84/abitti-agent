using Microsoft.Extensions.Hosting;

Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureServices(services =>
    {
        services.AddHostedService<AbittiAgent.Service.Worker>();
    })
    .Build()
    .Run();
