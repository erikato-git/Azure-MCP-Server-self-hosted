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
public sealed class ApplicationInsightsTools
{
    [McpServerTool, Description("Discover the Application Insights resource and its linked Log Analytics workspace in a resource group. Returns workspaceResourceId needed for RunApplicationInsightsQuery.")]
    public static async Task<string> GetApplicationInsightsWorkspace(
        IHttpContextAccessor httpContextAccessor,
        [Description("Name of the resource group containing the Application Insights resource.")] string resourceGroupName,
        [Description("Azure subscription ID. If omitted, auto-discovered from the resource group name.")] string subscriptionId = "")
    {
        var (credential, errorJson) = OboCredentialHelper.Create(httpContextAccessor);
        if (credential is null) return errorJson!;

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
                        $"Could not find resource group '{resourceGroupName}' in any of your Azure subscriptions.",
                        "Ensure you have Reader access on the subscription containing this resource group.");
                subscriptionId = discovered;
            }

            var armClient = new ArmClient(credential);
            var rgId = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
            var resourceGroup = armClient.GetResourceGroupResource(rgId);

            var aiResources = new List<(string Name, string ResourceId)>();
            await foreach (var resource in resourceGroup.GetGenericResourcesAsync(
                filter: "resourceType eq 'microsoft.insights/components'"))
            {
                aiResources.Add((resource.Data.Name, resource.Id.ToString()));
            }

            if (aiResources.Count == 0)
                return OboCredentialHelper.FailJson(
                    $"No Application Insights resources found in resource group '{resourceGroupName}'.",
                    "Ensure the resource group contains an Application Insights component.");

            if (aiResources.Count > 1)
                return JsonSerializer.Serialize(new
                {
                    error = true,
                    message = $"Found {aiResources.Count} Application Insights resources in '{resourceGroupName}'. Specify which one.",
                    availableResources = aiResources.Select(r => r.Name).ToArray()
                }, OboCredentialHelper.JsonOptions);

            var (aiName, aiResourceId) = aiResources[0];

            var aiResponse = await httpClient.GetAsync(
                $"https://management.azure.com{aiResourceId}?api-version=2020-02-02");
            aiResponse.EnsureSuccessStatusCode();
            using var aiDoc = JsonDocument.Parse(await aiResponse.Content.ReadAsStringAsync());
            var workspaceResourceId = aiDoc.RootElement
                .GetProperty("properties")
                .GetProperty("WorkspaceResourceId")
                .GetString()!;

            return JsonSerializer.Serialize(new
            {
                applicationInsightsName = aiName,
                applicationInsightsResourceId = aiResourceId,
                workspaceResourceId,
                resourceGroupName,
                subscriptionId
            }, OboCredentialHelper.JsonOptions);
        }
        catch (Exception ex)
        {
            return OboCredentialHelper.ErrorJson(
                $"Failed to discover Application Insights workspace: {ex.Message}",
                "Ensure your account has Reader access on the resource group.",
                new { subscriptionId, resourceGroupName });
        }
    }

    [McpServerTool, Description("Run a KQL query against an Application Insights Log Analytics workspace. Call GetApplicationInsightsWorkspace first to get the workspaceResourceId. Common tables: AppRequests, AppExceptions, AppDependencies, AppTraces, AppCustomEvents, AppAvailabilityResults, AppPageViews.")]
    public static async Task<string> RunApplicationInsightsQuery(
        IHttpContextAccessor httpContextAccessor,
        [Description("ARM resource ID of the Log Analytics workspace (workspaceResourceId from GetApplicationInsightsWorkspace).")] string workspaceResourceId,
        [Description("KQL query to execute.")] string kqlQuery,
        [Description("Time range in hours (e.g. 24 for last day, 168 for last 7 days). Default: 24.")] int timeRangeHours = 24)
    {
        var (credential, errorJson) = OboCredentialHelper.Create(httpContextAccessor);
        if (credential is null) return errorJson!;

        try
        {
            var armToken = await credential.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]),
                CancellationToken.None);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", armToken.Token);

            var body = JsonSerializer.Serialize(new { query = kqlQuery, timespan = $"PT{timeRangeHours}H" });
            var response = await httpClient.PostAsync(
                $"https://management.azure.com{workspaceResourceId}/api/query?api-version=2020-08-01",
                new StringContent(body, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();

            return ParseQueryResponse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            return OboCredentialHelper.ErrorJson(
                $"KQL query failed: {ex.Message}",
                "Ensure your account has Log Analytics Reader access on the workspace.",
                new { workspaceResourceId, kqlQuery });
        }
    }

    private static string ParseQueryResponse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var table = (doc.RootElement.TryGetProperty("Tables", out var t) ? t : doc.RootElement.GetProperty("tables"))[0];

        var columns = (table.TryGetProperty("Columns", out var cols) ? cols : table.GetProperty("columns"))
            .EnumerateArray()
            .Select(c => (c.TryGetProperty("ColumnName", out var n) ? n : c.GetProperty("name")).GetString()!)
            .ToArray();

        var rows = (table.TryGetProperty("Rows", out var r) ? r : table.GetProperty("rows"))
            .EnumerateArray()
            .Select(row => columns
                .Zip(row.EnumerateArray(), (col, el) => (col, val: el.Clone()))
                .ToDictionary(x => x.col, x => x.val))
            .ToList();

        return JsonSerializer.Serialize(new { rowCount = rows.Count, columns, rows }, OboCredentialHelper.JsonOptions);
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
}
