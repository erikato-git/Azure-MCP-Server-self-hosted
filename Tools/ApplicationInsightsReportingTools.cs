using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using QuickstartWeatherServer.Helpers;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QuickstartWeatherServer.Tools;

[McpServerToolType]
public sealed class ApplicationInsightsReportingTools
{
    private static readonly string[] ValidDataTypes =
        ["requests", "exceptions", "dependencies", "traces", "customEvents", "availabilityResults", "pageViews", "all"];

    [McpServerTool, Description("Generate an aggregated summary report from an Application Insights resource in a resource group. Auto-discovers the Application Insights resource and the subscription if not provided. Supports data types: requests, exceptions, dependencies, traces, customEvents, availabilityResults, pageViews, or all.")]
    public static async Task<string> GetApplicationInsightsReport(
        IHttpContextAccessor httpContextAccessor,
        [Description("The name of the resource group containing the Application Insights resource.")] string resourceGroupName,
        [Description("Data type: 'requests', 'exceptions', 'dependencies', 'traces', 'customEvents', 'availabilityResults', 'pageViews', or 'all'.")] string dataType,
        [Description("Time range value (e.g. 24 for last 24 hours, 7 for last 7 days).")] int timeRangeValue,
        [Description("Time range unit: 'hours' or 'days'.")] string timeRangeUnit,
        [Description("The Azure subscription ID. If omitted, the subscription is auto-discovered from the resource group name.")] string subscriptionId = "",
        [Description("Language for the report summary: 'en' for English (default) or 'da' for Danish.")] string language = "en")
    {
        if (!ValidDataTypes.Contains(dataType))
            return OboCredentialHelper.FailJson(
                $"Invalid dataType '{dataType}'.",
                $"Valid values are: {string.Join(", ", ValidDataTypes)}");

        var unit = timeRangeUnit.ToLower();
        if (unit != "hours" && unit != "days")
            return OboCredentialHelper.FailJson(
                $"Invalid timeRangeUnit '{timeRangeUnit}'.",
                "Valid values are: 'hours' or 'days'.");

        var (credential, errorJson) = OboCredentialHelper.Create(httpContextAccessor);
        if (credential is null) return errorJson!;

        bool isDanish = language.ToLower() == "da";
        var timeRangeLabel = isDanish
            ? $"Seneste {timeRangeValue} {(unit == "hours" ? "timer" : "dage")}"
            : $"Last {timeRangeValue} {(unit == "hours" ? "hours" : "days")}";

        try
        {
            var armToken = await credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                CancellationToken.None);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", armToken.Token);

            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                var discovered = await FindSubscriptionIdAsync(httpClient, resourceGroupName);
                if (discovered is null)
                    return OboCredentialHelper.FailJson(
                        isDanish
                            ? $"Kunne ikke finde resource group '{resourceGroupName}' i nogen af dine Azure-subscriptions."
                            : $"Could not find resource group '{resourceGroupName}' in any of your Azure subscriptions.",
                        "Ensure you have Reader access on the subscription containing this resource group.");
                subscriptionId = discovered;
            }

            var armClient = new ArmClient(credential);
            var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
            var resourceGroup = armClient.GetResourceGroupResource(rgId);

            var applicationInsightsResources = new List<(string Name, string ResourceId)>();
            await foreach (var resource in resourceGroup.GetGenericResourcesAsync(
                filter: "resourceType eq 'microsoft.insights/components'"))
            {
                applicationInsightsResources.Add((resource.Data.Name, resource.Id.ToString()));
            }

            if (applicationInsightsResources.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    found = false,
                    message = isDanish
                        ? $"Ingen Application Insights-ressourcer fundet i resource group '{resourceGroupName}'."
                        : $"No Application Insights resources found in resource group '{resourceGroupName}'.",
                    resourceGroupName,
                    subscriptionId
                }, OboCredentialHelper.JsonOptions);

            if (applicationInsightsResources.Count > 1)
                return JsonSerializer.Serialize(new
                {
                    found = false,
                    message = isDanish
                        ? $"Fandt {applicationInsightsResources.Count} Application Insights-ressourcer i '{resourceGroupName}'. Angiv venligst hvilken ressource du ønsker."
                        : $"Found {applicationInsightsResources.Count} Application Insights resources in '{resourceGroupName}'. Please specify which resource you want.",
                    availableResources = applicationInsightsResources.Select(r => r.Name).ToArray(),
                    hint = isDanish
                        ? "Brug ListServicesInResourceGroup til at identificere den ønskede Application Insights-ressource."
                        : "Use ListServicesInResourceGroup to identify the desired Application Insights resource."
                }, OboCredentialHelper.JsonOptions);

            var (aiName, aiResourceId) = applicationInsightsResources[0];

            // Resolve the linked Log Analytics workspace resource ID from the Application Insights component.
            var applicationInsightsComponentResponse = await httpClient.GetAsync(
                $"https://management.azure.com{aiResourceId}?api-version=2020-02-02");
            applicationInsightsComponentResponse.EnsureSuccessStatusCode();
            using var aiDoc = JsonDocument.Parse(await applicationInsightsComponentResponse.Content.ReadAsStringAsync());
            var workspaceResourceId = aiDoc.RootElement
                .GetProperty("properties")
                .GetProperty("WorkspaceResourceId")
                .GetString()!;

            var typesToQuery = dataType == "all"
                ? ValidDataTypes[..^1]
                : (string[])[dataType];

            var reports = new Dictionary<string, object?>();
            foreach (var type in typesToQuery)
                reports[type] = await QueryDataType(httpClient, workspaceResourceId, type.ToLower(), timeRangeValue, unit, isDanish);

            return JsonSerializer.Serialize(new
            {
                applicationInsightsResource = aiName,
                resourceGroup = resourceGroupName,
                subscriptionId,
                timeRange = timeRangeLabel,
                dataTypes = typesToQuery,
                report = reports
            }, OboCredentialHelper.JsonOptions);
        }
        catch (Exception ex)
        {
            return OboCredentialHelper.ErrorJson(
                $"Failed to generate Application Insights report: {ex.Message}",
                $"Ensure your account has Monitoring Reader access on the Application Insights resource in '{resourceGroupName}'.",
                new { subscriptionId, resourceGroupName });
        }
    }

    private static async Task<object?> QueryDataType(
        HttpClient httpClient, string applicationInsightResourceId, string dataType,
        int timeRangeValue, string timeRangeUnit, bool isDanish)
    {
        try
        {
            return dataType switch
            {
                "requests" => await QueryRequests(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                "exceptions" => await QueryExceptions(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                "dependencies" => await QueryDependencies(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                "traces" => await QueryTraces(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                "customevents" => await QueryCustomEvents(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                "availabilityresults" => await QueryAvailability(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                "pageviews" => await QueryPageViews(httpClient, applicationInsightResourceId, timeRangeValue, timeRangeUnit, isDanish),
                _ => (object?)null
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static async Task<QueryResult> RunKql(
        HttpClient client, string workspaceResourceId, string kql, int timeRangeValue, string timeRangeUnit)
    {
        var timespan = timeRangeUnit == "hours" ? $"PT{timeRangeValue}H" : $"P{timeRangeValue}D";
        var body = JsonSerializer.Serialize(new { query = kql, timespan });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var url = $"https://management.azure.com{workspaceResourceId}/api/query?api-version=2020-08-01";
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        return QueryResult.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> FindSubscriptionIdAsync(HttpClient httpClient, string resourceGroupName)
    {
        var response = await httpClient.GetAsync(
            "https://management.azure.com/subscriptions?api-version=2022-12-01");
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var subscriptionIds = doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(s => s.GetProperty("subscriptionId").GetString()!)
            .ToList();

        foreach (var subId in subscriptionIds)
        {
            var rgResponse = await httpClient.GetAsync(
                $"https://management.azure.com/subscriptions/{subId}/resourceGroups/{resourceGroupName}?api-version=2022-12-01");
            if (rgResponse.IsSuccessStatusCode) return subId;
        }

        return null;
    }

    private static string SeverityName(int level) => level switch
    {
        0 => "Verbose",
        1 => "Information",
        2 => "Warning",
        3 => "Error",
        4 => "Critical",
        _ => "Unknown"
    };

    private static async Task<object> QueryRequests(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppRequests
            | summarize
                totalRequests = count(),
                failedRequests = countif(Success == false),
                avgDurationMs = round(avg(DurationMs), 0),
                p95DurationMs = round(percentile(DurationMs, 95), 0),
                p99DurationMs = round(percentile(DurationMs, 99), 0)
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen anmodningsdata fundet." : "No request data found." };

        var total = r.GetInt64(0, "totalRequests") ?? 0;
        var failed = r.GetInt64(0, "failedRequests") ?? 0;
        var avg = r.GetDouble(0, "avgDurationMs") ?? 0;
        var p95 = r.GetDouble(0, "p95DurationMs") ?? 0;
        var p99 = r.GetDouble(0, "p99DurationMs") ?? 0;
        var successRate = total > 0 ? Math.Round(100.0 * (total - failed) / total, 1) : 0;

        var summary = isDanish
            ? $"Applikationen modtog {total:N0} anmodninger med en succesrate på {successRate}% og en gennemsnitlig svartid på {avg:N0} ms (P95: {p95:N0} ms, P99: {p99:N0} ms)."
            : $"The application received {total:N0} requests with a {successRate}% success rate and an average response time of {avg:N0} ms (P95: {p95:N0} ms, P99: {p99:N0} ms).";

        return new { summary, metrics = new { totalRequests = total, failedRequests = failed, successRatePct = successRate, avgDurationMs = avg, p95DurationMs = p95, p99DurationMs = p99 } };
    }

    private static async Task<object> QueryTraces(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppTraces
            | summarize occurrences = count() by SeverityLevel, Message
            | order by SeverityLevel desc, occurrences desc
            | take 20
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen trace-data fundet." : "No trace data found." };

        var entries = r.Rows().Select(i => new
        {
            severityLevel = (int)(r.GetInt64(i, "SeverityLevel") ?? 0),
            message = r.GetString(i, "Message"),
            occurrences = r.GetInt64(i, "occurrences") ?? 0
        }).ToList();

        var total = entries.Sum(e => e.occurrences);
        var bySeverity = entries
            .GroupBy(e => e.severityLevel)
            .Select(g => new { severityLevel = g.Key, severityName = SeverityName(g.Key), count = g.Sum(e => e.occurrences) })
            .OrderByDescending(x => x.severityLevel)
            .ToList();

        var errors = bySeverity.Where(s => s.severityLevel >= 3).Sum(s => s.count);
        var warnings = bySeverity.Where(s => s.severityLevel == 2).Sum(s => s.count);

        var summary = isDanish
            ? $"Applikationen registrerede {total:N0} trace-hændelser: {errors:N0} fejl/kritiske, {warnings:N0} advarsler."
            : $"The application logged {total:N0} trace events: {errors:N0} errors/critical, {warnings:N0} warnings.";

        var topMessages = entries.Select(e => new { severity = SeverityName(e.severityLevel), e.message, e.occurrences }).ToList();
        return new { summary, bySeverity, topMessages };
    }

    private static async Task<object> QueryExceptions(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppExceptions
            | summarize occurrences = count() by ExceptionType, OuterMessage
            | order by occurrences desc
            | take 10
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen undtagelser fundet." : "No exceptions found." };

        var total = r.Rows().Sum(i => r.GetInt64(i, "occurrences") ?? 0);
        var top = r.Rows().Select(i => new
        {
            type = r.GetString(i, "ExceptionType"),
            message = r.GetString(i, "OuterMessage"),
            occurrences = r.GetInt64(i, "occurrences") ?? 0
        }).ToList();

        var summary = isDanish
            ? $"Der opstod {total:N0} undtagelser i alt. Den hyppigste er '{top[0].type}' med {top[0].occurrences:N0} forekomster."
            : $"A total of {total:N0} exceptions occurred. The most frequent is '{top[0].type}' with {top[0].occurrences:N0} occurrences.";

        return new { summary, topExceptions = top };
    }

    private static async Task<object> QueryDependencies(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppDependencies
            | summarize
                calls = count(),
                avgDurationMs = round(avg(DurationMs), 0),
                failedCalls = countif(Success == false)
              by Type, Target
            | order by calls desc
            | take 10
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen afhængighedsdata fundet." : "No dependency data found." };

        var total = r.Rows().Sum(i => r.GetInt64(i, "calls") ?? 0);
        var top = r.Rows().Select(i => new
        {
            type = r.GetString(i, "Type"),
            target = r.GetString(i, "Target"),
            calls = r.GetInt64(i, "calls") ?? 0,
            avgDurationMs = r.GetDouble(i, "avgDurationMs") ?? 0,
            failedCalls = r.GetInt64(i, "failedCalls") ?? 0
        }).ToList();

        var summary = isDanish
            ? $"Applikationen foretog {total:N0} kald til eksterne afhængigheder. Den mest brugte er '{top[0].target}' ({top[0].calls:N0} kald)."
            : $"The application made {total:N0} calls to external dependencies. The most used is '{top[0].target}' ({top[0].calls:N0} calls).";

        return new { summary, topDependencies = top };
    }

    private static async Task<object> QueryCustomEvents(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppCustomEvents
            | summarize occurrences = count() by Name
            | order by occurrences desc
            | take 10
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen brugerdefinerede hændelser fundet." : "No custom events found." };

        var total = r.Rows().Sum(i => r.GetInt64(i, "occurrences") ?? 0);
        var events = r.Rows().Select(i => new
        {
            name = r.GetString(i, "Name"),
            occurrences = r.GetInt64(i, "occurrences") ?? 0
        }).ToList();

        var summary = isDanish
            ? $"Der blev registreret {total:N0} brugerdefinerede hændelser. Den hyppigste er '{events[0].name}' med {events[0].occurrences:N0} forekomster."
            : $"A total of {total:N0} custom events were recorded. The most frequent is '{events[0].name}' with {events[0].occurrences:N0} occurrences.";

        return new { summary, topEvents = events };
    }

    private static async Task<object> QueryAvailability(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppAvailabilityResults
            | summarize
                totalTests = count(),
                failedTests = countif(Success == false),
                avgDurationMs = round(avg(DurationMs), 0)
              by Name, Location
            | order by failedTests desc
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen tilgængelighedsdata fundet." : "No availability data found." };

        var totalTests = r.Rows().Sum(i => r.GetInt64(i, "totalTests") ?? 0);
        var failedTests = r.Rows().Sum(i => r.GetInt64(i, "failedTests") ?? 0);
        var availPct = totalTests > 0 ? Math.Round(100.0 * (totalTests - failedTests) / totalTests, 1) : 0;
        var tests = r.Rows().Select(i => new
        {
            name = r.GetString(i, "Name"),
            location = r.GetString(i, "Location"),
            totalTests = r.GetInt64(i, "totalTests") ?? 0,
            failedTests = r.GetInt64(i, "failedTests") ?? 0,
            avgDurationMs = r.GetDouble(i, "avgDurationMs") ?? 0
        }).ToList();

        var summary = isDanish
            ? $"Tilgængeligheds-tests viser {availPct}% samlet tilgængelighed baseret på {totalTests:N0} tests ({failedTests:N0} fejlede)."
            : $"Availability tests show {availPct}% overall availability based on {totalTests:N0} tests ({failedTests:N0} failed).";

        return new { summary, availabilityByTest = tests };
    }

    private static async Task<object> QueryPageViews(
        HttpClient client, string rid, int tv, string tu, bool isDanish)
    {
        const string kql = """
            AppPageViews
            | summarize
                views = count(),
                avgDurationMs = round(avg(DurationMs), 0)
              by Name
            | order by views desc
            | take 10
            """;

        var r = await RunKql(client, rid, kql, tv, tu);
        if (r.RowCount == 0)
            return new { summary = isDanish ? "Ingen sidevisningsdata fundet." : "No page view data found." };

        var total = r.Rows().Sum(i => r.GetInt64(i, "views") ?? 0);
        var pages = r.Rows().Select(i => new
        {
            name = r.GetString(i, "Name"),
            views = r.GetInt64(i, "views") ?? 0,
            avgDurationMs = r.GetDouble(i, "avgDurationMs") ?? 0
        }).ToList();

        var summary = isDanish
            ? $"Applikationen registrerede {total:N0} sidevisninger. Den mest besøgte side er '{pages[0].name}' med {pages[0].views:N0} visninger."
            : $"The application recorded {total:N0} total page views. The most visited page is '{pages[0].name}' with {pages[0].views:N0} views.";

        return new { summary, topPages = pages };
    }

    private sealed class QueryResult
    {
        private readonly string[] _columns;
        private readonly JsonElement[][] _rows;

        private QueryResult(string[] columns, JsonElement[][] rows)
        {
            _columns = columns;
            _rows = rows;
        }

        public int RowCount => _rows.Length;
        public IEnumerable<int> Rows() => Enumerable.Range(0, _rows.Length);

        public long? GetInt64(int row, string column)
        {
            var idx = Array.IndexOf(_columns, column);
            if (idx < 0 || row >= _rows.Length) return null;
            var el = _rows[row][idx];
            if (el.ValueKind == JsonValueKind.Null) return null;
            if (el.TryGetInt64(out var v)) return v;
            if (el.TryGetDouble(out var d)) return (long)d;
            return null;
        }

        public double? GetDouble(int row, string column)
        {
            var idx = Array.IndexOf(_columns, column);
            if (idx < 0 || row >= _rows.Length) return null;
            var el = _rows[row][idx];
            if (el.ValueKind == JsonValueKind.Null) return null;
            if (el.TryGetDouble(out var v)) return v;
            if (el.TryGetInt64(out var l)) return l;
            return null;
        }

        public string? GetString(int row, string column)
        {
            var idx = Array.IndexOf(_columns, column);
            if (idx < 0 || row >= _rows.Length) return null;
            var el = _rows[row][idx];
            return el.ValueKind == JsonValueKind.Null ? null : el.GetString();
        }

        public static QueryResult Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // /api/query returns "Tables" (PascalCase); api.loganalytics.io returns "tables".
            var table = (root.TryGetProperty("Tables", out var t) ? t : root.GetProperty("tables"))[0];

            var columns = (table.TryGetProperty("Columns", out var cols) ? cols : table.GetProperty("columns"))
                .EnumerateArray()
                .Select(c => (c.TryGetProperty("ColumnName", out var cn) ? cn : c.GetProperty("name")).GetString()!)
                .ToArray();

            var rows = (table.TryGetProperty("Rows", out var r) ? r : table.GetProperty("rows"))
                .EnumerateArray()
                .Select(row => row.EnumerateArray().Select(el => el.Clone()).ToArray())
                .ToArray();

            return new QueryResult(columns, rows);
        }
    }
}
