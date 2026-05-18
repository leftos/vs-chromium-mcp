using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using VsChromiumMcp;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => {
    // MCP uses stdout for protocol messages; logs must go to stderr.
    o.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<DaemonHandle>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<VsChromiumTools>();

await builder.Build().RunAsync();
