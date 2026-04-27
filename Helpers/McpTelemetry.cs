using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace QuickstartWeatherServer.Helpers;

internal static class McpTelemetry
{
    internal const string ServiceName = "AzureMcpServer";
    internal static readonly ActivitySource ActivitySource = new(ServiceName);
    internal static readonly Meter Meter = new(ServiceName);
}
