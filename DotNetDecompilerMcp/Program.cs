using DotNetDecompilerMcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Suppress all logs to stdout — MCP uses stdio, logs must go to stderr only
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Register DecompilerService as singleton so the cache persists across all tool calls.
// Tool classes (AssemblyTools, TypeTools, etc.) are picked up automatically by
// WithToolsFromAssembly() via [McpServerToolType] and resolved from DI, so they will
// receive this singleton through constructor injection.
builder.Services.AddSingleton<DecompilerService>();
builder.Services.AddSingleton<DatabaseService>();

// Register MCP server with stdio transport.
// WithToolsFromAssembly scans for [McpServerToolType] classes in this assembly.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
