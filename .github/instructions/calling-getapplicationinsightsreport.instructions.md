---
applyTo: "**"
---

# Calling GetApplicationInsightsReport on remote-mcp-server

The `GetApplicationInsightsReport` tool generates an aggregated summary report from an Application Insights resource. It auto-discovers the Application Insights resource within the resource group, and can auto-discover the subscription ID if it is not provided. It works only when deployed to Azure.

## What the tool reports

| Data type | Description |
|---|---|
| `requests` | Total calls, success rate, avg/P95/P99 response times |
| `exceptions` | Top exceptions by type and message |
| `dependencies` | Outgoing calls by target and type |
| `traces` | Log events grouped by severity level and message |
| `customEvents` | Custom event counts by name |
| `availabilityResults` | Availability test pass/fail rates |
| `pageViews` | Page view counts (web apps only) |
| `all` | All of the above in one call |

Data types with no telemetry in the workspace return a "no data found" message rather than an error.

## Minimal invocation — subscription auto-discovered

The tool only requires the resource group name. The subscription ID is optional and will be discovered automatically by searching all subscriptions the authenticated user can access.

**Example prompt:**
> "Giv mig et referat af hændelserne i AzureMcpServer de sidste 24 timer"

Maps to:

```json
{
  "resourceGroupName": "AzureMcpServer",
  "dataType": "all",
  "timeRangeValue": 24,
  "timeRangeUnit": "hours",
  "language": "da"
}
```

## All parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `resourceGroupName` | Yes | — | Name of the resource group containing the Application Insights resource |
| `dataType` | Yes | — | `requests`, `exceptions`, `dependencies`, `traces`, `customEvents`, `availabilityResults`, `pageViews`, or `all` |
| `timeRangeValue` | Yes | — | Numeric value (e.g. `24`, `7`) |
| `timeRangeUnit` | Yes | — | `hours` or `days` |
| `subscriptionId` | No | auto-discovered | Azure subscription ID (GUID). Omit to let the tool find it automatically |
| `language` | No | `en` | `en` for English, `da` for Danish |

## How auto-discovery works

When `subscriptionId` is omitted, the tool:
1. Lists all subscriptions the authenticated user has access to via `GET /subscriptions`
2. For each subscription, checks whether the named resource group exists
3. Uses the first matching subscription

If the resource group is not found in any subscription, the tool returns an error.

## Calling via curl (Claude Code / headless)

```bash
# Step 1: Get token scoped to the MCP server's Entra app
TOKEN=$(az account get-access-token \
  --resource "api://<function-app-id-uri>" \
  --query accessToken -o tsv)

# Step 2: Call the tool — subscription ID is optional
curl -s -X POST "https://<function-app>.azurewebsites.net/mcp" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "GetApplicationInsightsReport",
      "arguments": {
        "resourceGroupName": "AzureMcpServer",
        "dataType": "all",
        "timeRangeValue": 24,
        "timeRangeUnit": "hours",
        "language": "da"
      }
    }
  }'
```

Replace `<function-app-id-uri>` with the value from `allowedAudiences` in Easy Auth settings (e.g. `api://func-mcp-tbgbe7twyg3ii-app-7ibzn45rcttbq`), and `<function-app>` with the Function App hostname.

## Expected response

```json
{
  "applicationInsightsResource": "appi-<suffix>",
  "resourceGroup": "AzureMcpServer",
  "subscriptionId": "<auto-discovered-or-provided>",
  "timeRange": "Seneste 24 timer",
  "dataTypes": ["requests", "exceptions", "dependencies", "traces", "customEvents", "availabilityResults", "pageViews"],
  "report": {
    "requests": {
      "summary": "Applikationen modtog 39 anmodninger med en succesrate på 100% ...",
      "metrics": {
        "totalRequests": 39,
        "failedRequests": 0,
        "successRatePct": 100,
        "avgDurationMs": 570,
        "p95DurationMs": 2932,
        "p99DurationMs": 5172
      }
    },
    "traces": {
      "summary": "Applikationen registrerede 272 trace-hændelser: 0 fejl/kritiske, 0 advarsler.",
      "bySeverity": [
        { "severityLevel": 1, "severityName": "Information", "count": 272 }
      ],
      "topMessages": [...]
    },
    "exceptions": { "summary": "Ingen undtagelser fundet." }
  }
}
```

## Notes on this specific deployment

- The workspace uses workspace-based Application Insights (ingestion mode: `LogAnalytics`), so the tool queries the `App*` table family (`AppRequests`, `AppTraces`, `AppExceptions`, etc.) via the Log Analytics workspace ARM endpoint.
- The ARM token obtained via OBO is sufficient — no separate `api.loganalytics.io` permission is required.
- `customEvents` and `pageViews` return `400 Bad Request` if the tables do not exist in the workspace (i.e. no telemetry of that type has ever been sent). This is expected for a Functions-based MCP server.
- The Function App cold-starts frequently on Flex Consumption, which inflates P95/P99 response times and produces repeating startup trace messages.

## Troubleshooting

### `400 Bad Request` on a data type

The Log Analytics table for that data type (`AppCustomEvents`, `AppPageViews`, etc.) does not exist because no telemetry of that type has been ingested. This is not an error in the tool — it reflects the application not emitting that data.

### `Could not find resource group '...' in any of your Azure subscriptions`

The authenticated user does not have Reader access on the subscription containing the resource group, or the resource group name is misspelled.

### `OnBehalfOfCredential authentication failed`

The OBO token exchange failed. Re-authenticate and ensure consent has been granted:

```bash
az logout
az login --tenant "<your-tenant-id>" --scope "api://<function-app-id-uri>/.default"
```

### `No Application Insights resources found`

The resource group exists but contains no `microsoft.insights/components` resources. Verify with:

```bash
az resource list --resource-group <resourceGroupName> --resource-type microsoft.insights/components
```
