using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using QuickstartWeatherServer.Extensions;
using QuickstartWeatherServer.Helpers;
using QuickstartWeatherServer.Tools;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:8080");

var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrEmpty(appInsightsConnectionString))
{
    var miClientId = builder.Configuration["OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID"];
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddSource(McpTelemetry.ServiceName))
        .WithMetrics(metrics => metrics.AddMeter(McpTelemetry.ServiceName))
        .UseAzureMonitor(options =>
        {
            if (!string.IsNullOrEmpty(miClientId))
                options.Credential = new ManagedIdentityCredential(miClientId);
        });
}

builder.Services.AddMcpServer()
    .WithHttpTransport((options) =>
    {
        options.Stateless = true;
    })
    .WithTools<WeatherTools>()
    .WithTools<UserInfoTools>()
    .WithTools<ListResourceGroupServicesTools>()
    .WithTools<ApplicationInsightsTools>();

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Error;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpRateLimiting();

builder.Services.AddSingleton(_ =>
{
    var client = new HttpClient { BaseAddress = new Uri("https://api.weather.gov") };
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("weather-tool", "1.0"));
    return client;
});

var app = builder.Build();

app.UseRateLimiter();

// Add root endpoint
app.MapGet("/", () => "Custom handler is ready and running.");

// Add health check endpoint
app.MapGet("/api/healthz", () => "Healthy");

// Add authcomplete endpoint
app.MapGet("/authcomplete", () => Results.Content(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "authcomplete.html")),
    "text/html"
));

// Map MCP endpoints
app.MapMcp(pattern: "/mcp").RequireMcpRateLimiting();

await app.RunAsync();
