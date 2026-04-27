using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using QuickstartWeatherServer.Helpers;
using System.ComponentModel;
using System.Text.Json;

namespace QuickstartWeatherServer.Tools;

[McpServerToolType]
public sealed class ListResourceGroupServicesTools
{
    [McpServerTool, Description("List all Azure resource groups in a subscription that the current user has access to. Results are grouped by Azure region.")]
    public static async Task<string> ListResourceGroups(
        IHttpContextAccessor httpContextAccessor,
        [Description("The Azure subscription ID.")] string subscriptionId,
        [Description("Optional: comma-separated tag filters in 'key=value' format (e.g. 'environment=prod,team=backend'). Multiple filters are ANDed. Leave empty to return all resource groups.")] string? tagFilters = null)
    {
        var (credential, errorJson) = OboCredentialHelper.Create(httpContextAccessor);
        if (credential is null) return errorJson!;

        try
        {
            var tagFilter = ParseTagFilters(tagFilters);
            var armClient = new ArmClient(credential);
            var subscription = armClient.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(subscriptionId));

            var result = new List<ResourceGroupEntry>();
            await foreach (var rg in subscription.GetResourceGroups().GetAllAsync())
            {
                if (tagFilter.Count > 0 && !tagFilter.All(kv =>
                    rg.Data.Tags != null &&
                    rg.Data.Tags.TryGetValue(kv.Key, out var v) && v == kv.Value))
                    continue;

                result.Add(new ResourceGroupEntry(
                    rg.Data.Name,
                    rg.Data.Location.DisplayName ?? rg.Data.Location.Name,
                    rg.Data.Tags ?? new Dictionary<string, string>()));
            }

            var byRegion = result
                .GroupBy(rg => rg.Location)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(rg => new
                    {
                        name = rg.Name,
                        tags = rg.Tags
                    }).ToList());

            return JsonSerializer.Serialize(new
            {
                subscriptionId,
                totalResourceGroups = result.Count,
                appliedTagFilters = tagFilter.Count > 0 ? tagFilter : null,
                byRegion
            }, OboCredentialHelper.JsonOptions);
        }
        catch (Exception ex)
        {
            return OboCredentialHelper.ErrorJson(
                $"Failed to list resource groups: {ex.Message}",
                $"Ensure your account has at least Reader access on subscription '{subscriptionId}'.",
                new { subscriptionId });
        }
    }

    [McpServerTool, Description("List all Azure services (resources) in a specific resource group, grouped by service type.")]
    public static async Task<string> ListServicesInResourceGroup(
        IHttpContextAccessor httpContextAccessor,
        [Description("The Azure subscription ID.")] string subscriptionId,
        [Description("The name of the resource group to inspect.")] string resourceGroupName)
    {
        var (credential, errorJson) = OboCredentialHelper.Create(httpContextAccessor);
        if (credential is null) return errorJson!;

        try
        {
            var armClient = new ArmClient(credential);
            var rgIdentifier = ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
            var resourceGroup = armClient.GetResourceGroupResource(rgIdentifier);

            var resources = new List<ResourceEntry>();
            await foreach (var resource in resourceGroup.GetGenericResourcesAsync())
            {
                var type = resource.Data.ResourceType.ToString();
                resources.Add(new ResourceEntry(
                    resource.Data.Name,
                    FriendlyType(type),
                    resource.Data.Location.DisplayName ?? resource.Data.Location.Name,
                    resource.Data.Tags ?? new Dictionary<string, string>()));
            }

            var byServiceType = resources
                .GroupBy(r => r.FriendlyType)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => new
                    {
                        name = r.Name,
                        location = r.Location,
                        tags = r.Tags
                    }).ToList());

            return JsonSerializer.Serialize(new
            {
                resourceGroupName,
                subscriptionId,
                totalResources = resources.Count,
                byServiceType
            }, OboCredentialHelper.JsonOptions);
        }
        catch (Exception ex)
        {
            return OboCredentialHelper.ErrorJson(
                $"Failed to list services in resource group '{resourceGroupName}': {ex.Message}",
                "Ensure the resource group exists and your account has at least Reader access. Use ListResourceGroups to see available resource groups.",
                new { subscriptionId, resourceGroupName });
        }
    }

    private static Dictionary<string, string> ParseTagFilters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim());
    }

    private static string FriendlyType(string resourceType) => resourceType switch
    {
        "Microsoft.Web/sites" => "App Service",
        "Microsoft.Web/serverFarms" => "App Service Plan",
        "Microsoft.Web/staticSites" => "Static Web App",
        "Microsoft.Storage/storageAccounts" => "Storage Account",
        "Microsoft.Sql/servers" => "SQL Server",
        "Microsoft.Sql/servers/databases" => "SQL Database",
        "Microsoft.KeyVault/vaults" => "Key Vault",
        "Microsoft.Insights/components" => "Application Insights",
        "Microsoft.OperationalInsights/workspaces" => "Log Analytics Workspace",
        "Microsoft.Network/virtualNetworks" => "Virtual Network",
        "Microsoft.Network/networkSecurityGroups" => "Network Security Group",
        "Microsoft.Network/privateEndpoints" => "Private Endpoint",
        "Microsoft.Compute/virtualMachines" => "Virtual Machine",
        "Microsoft.ContainerRegistry/registries" => "Container Registry",
        "Microsoft.ManagedIdentity/userAssignedIdentities" => "Managed Identity",
        "Microsoft.EventHub/namespaces" => "Event Hub Namespace",
        "Microsoft.ServiceBus/namespaces" => "Service Bus Namespace",
        "Microsoft.Cache/Redis" => "Redis Cache",
        "Microsoft.DocumentDB/databaseAccounts" => "Cosmos DB",
        "Microsoft.ContainerService/managedClusters" => "AKS Cluster",
        "Microsoft.App/containerApps" => "Container App",
        "Microsoft.App/managedEnvironments" => "Container Apps Environment",
        "Microsoft.Authorization/roleAssignments" => "Role Assignment",
        _ => resourceType.Contains('/') ? string.Join(" / ", resourceType.Split('/').Skip(1)) : resourceType
    };

    private record ResourceGroupEntry(string Name, string Location, IDictionary<string, string> Tags);
    private record ResourceEntry(string Name, string FriendlyType, string Location, IDictionary<string, string> Tags);
}
