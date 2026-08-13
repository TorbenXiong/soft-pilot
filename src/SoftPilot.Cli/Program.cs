using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoftPilot.Cli;
using SoftPilot.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSoftPilot();
builder.Services.AddSingleton<CliApplication>();
using var host = builder.Build();

try
{
    await host.Services.InitializeSoftPilotAsync();
    return await host.Services.GetRequiredService<CliApplication>().RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"spt: {exception.Message}");
    return 1;
}
