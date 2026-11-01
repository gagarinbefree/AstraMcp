using AstraMcp.Core;
using AstraMcp.Infrastructure;
using AstraMcp.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var configurationPath = args switch
{
    [] => Path.Combine(AppContext.BaseDirectory, "config.json"),
    ["--config", var path] => Path.GetFullPath(path),
    _ => throw new ArgumentException("Usage: AstraMcp.exe [--config <configuration.json>]")
};
var configurationDirectory = Path.GetDirectoryName(configurationPath)!;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = configurationDirectory });
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile(configurationPath, optional: false, reloadOnChange: false);
var options = builder.Configuration.GetSection("Astra").Get<AdapterOptions>() ?? throw new InvalidOperationException("Configuration requires an Astra section.");
options.Normalize(configurationDirectory);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDirectory, "logs")));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IJobStore, SqliteJobStore>();
builder.Services.AddSingleton<Artifacts>();
builder.Services.AddSingleton<IAstraProcess, AstraProcess>();
builder.Services.AddSingleton<JobExecutor>();
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();
await builder.Build().RunAsync();
