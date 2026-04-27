---
applyTo: "**"
---

# Calling ListResourceGroups via Azure MCP tools

The `ListResourceGroups` tool (from `ListResourceGroupServicesTools.cs`) retrieves all Azure resource groups in a subscription that the authenticated user has access to. Results are grouped by Azure region.

## Two ways to call ListResourceGroups

### Option 1: Remote MCP server (via `.vscode/mcp.json`)

The `remote-mcp-server` configured in `.vscode/mcp.json` exposes this tool when deployed to Azure. However, it requires:
- Easy Auth configured on the Function App
- The Entra app registration properly set up in your tenant
- A valid bearer token exchanged via OnBehalfOfCredential (OBO flow)

**Issue:** If you see `AADSTS500011` (resource principal not found), the Entra app registration may not exist or consent was not granted. Ensure `azd up` completed successfully before attempting remote calls.

### Option 2: Azure CLI credential chain (recommended for testing)

Use the built-in `mcp_azure_mcp_group_list` tool, which leverages Azure CLI authentication directly. This is simpler for local testing and troubleshooting.

## Prerequisites

- Azure CLI (`az`) installed
- Authenticated to the correct tenant and subscription

## Steps

### Step 1: Authenticate to the correct tenant

```bash
az login --tenant "<your-tenant-id>"
```

Replace `<your-tenant-id>` with your Microsoft Entra tenant ID (a GUID). This opens a browser for interactive authentication.

**Example:** Run `az account show` to find your subscription ID and tenant ID, then use the tenant ID for the `--tenant` flag.

### Step 2: Call ListResourceGroups

Via Azure CLI tool (direct):

```bash
az account get-access-token --subscription "<subscription-id>" --query accessToken -o tsv
```

Then call the tool with:
- `subscription`: Your subscription ID (GUID)
- `tenant`: Your tenant ID (GUID) — optional but recommended for cross-tenant scenarios

### Step 3: Interpret the response

On success, you get a JSON object with:

```json
{
  "status": 200,
  "message": "Success",
  "results": {
    "groups": [
      {
        "name": "MyResourceGroup",
        "id": "/subscriptions/<subscription-id>/resourceGroups/MyResourceGroup",
        "location": "northeurope"
      }
    ]
  }
}
```

Each resource group includes:
- **name** — The display name
- **id** — The full Azure resource ID
- **location** — The Azure region

## Authentication errors and solutions

### "Tenant mismatch" error

**Cause:** You're authenticated to a different tenant than the subscription.

**Solution:**
```bash
az logout
az login --tenant "<correct-tenant-id>"
```

Then retry the call with the matching tenant ID.

### "InvalidAuthenticationTokenTenant" (401)

**Cause:** The access token was issued by the wrong tenant.

**Solution:** Verify the tenant ID on your subscription:

```bash
az account show --subscription "<subscription-id>"
```

Look at the `tenantId` field and log in to that tenant:

```bash
az logout
az login --tenant "<tenantId>"
```

### "AADSTS500011: resource principal not found"

**Cause:** (Remote server only) The Entra app registration is missing or not consented.

**Solution:** Redeploy the remote server and ensure consent is granted:

```bash
azd up
```

## Local development

For local development of the MCP server (`dotnet run`), the tool does not work without Easy Auth. Test with `GetAlerts` or `GetForecast` instead — they require no authentication.

To test `ListResourceGroups` locally, you must:
1. Deploy to Azure (`azd up`)
2. Call the remote endpoint with an authenticated token
3. Or use the Azure CLI credential chain directly (Option 2)

## Calling from AI agents (Claude Code, GitHub Copilot)

In VS Code chat (Claude Code or GitHub Copilot):

1. **Set up authentication:**
   ```bash
   az login --tenant "<your-tenant-id>"
   ```

2. **Ask the AI to call the tool:**
   - For remote server: "Connect to remote-mcp-server and call ListResourceGroups with subscription `<subscription-id>`"
   - For Azure CLI tools: "List all resource groups in subscription `<subscription-id>`"

The AI will use the authenticated session to retrieve your resource groups.

## Complete example

```bash
# Authenticate to your tenant
az login --tenant "<your-tenant-id>"

# Call ListResourceGroups (using Azure MCP tools)
# The tool uses your authenticated Azure CLI session
# Provide your subscription ID (replace with actual GUID)
az account get-access-token --subscription "<your-subscription-id>"
```

The response lists all resource groups you have access to, grouped by region.
