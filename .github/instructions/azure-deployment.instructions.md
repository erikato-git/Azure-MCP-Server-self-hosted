---
applyTo: "**"
---

# Azure deployment workflow

Deploys the MCP server as an Azure Functions (Flex Consumption) app with Easy Auth (Entra ID) in front of it. The MCP endpoint becomes `https://<functionapp>.azurewebsites.net/mcp` and is protected by Entra login — no anonymous access.

## Prerequisites

- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) — orchestrates provisioning and deployment
- [Azure CLI (`az`)](https://learn.microsoft.com/cli/azure/install-azure-cli) — required for credential resolution; `azd` alone is not sufficient for personal Microsoft accounts (MSA)
- .NET 8 SDK
- An Azure subscription with Owner or Contributor role

Register the `Microsoft.App` resource provider once per subscription (required for Flex Consumption):
```bash
az provider register --namespace Microsoft.App
```

## One-time setup

**1. Log in with Azure CLI**

```bash
az login --tenant <your-tenant-id>
```

This opens a browser. Sign in with the account that owns the Azure subscription. Verify the correct subscription is active:

```bash
az account show
```

Note the `id` field — this is your **subscription ID** (different from the tenant ID).

**2. Log in with azd**

```bash
azd auth login --tenant-id <your-tenant-id>
```

**3. Create the azd environment**

```bash
azd env new prod
```

`prod` is the environment name used internally by `azd` to generate unique resource name suffixes. It does not affect the Azure resource group name.

**4. Set deployment parameters**

```bash
azd env set AZURE_SUBSCRIPTION_ID <your-subscription-id>
azd env set AZURE_LOCATION northeurope
azd env set AZURE_RESOURCE_GROUP_NAME <your-resource-group-name>
azd env set VNET_ENABLED false
azd env set PRE_AUTHORIZED_CLIENT_IDS aebc6443-996d-45c2-90f0-388ff96faa56
```

- `AZURE_LOCATION` — any region that supports Flex Consumption (see `infra/main.bicep` for the full allowed list)
- `AZURE_RESOURCE_GROUP_NAME` — the resource group is created by the Bicep template; choose any name
- `VNET_ENABLED false` — deploys without VNet and private endpoints; storage is secured by Managed Identity auth instead of network isolation. Simpler and cheaper for getting started; can be enabled later
- `PRE_AUTHORIZED_CLIENT_IDS` — pre-authorises the VS Code OAuth client so GitHub Copilot and the Claude Code VS Code extension can connect without an admin consent prompt

## Deploy

```bash
azd up
```

`azd up` runs `azd package` (builds and zips the .NET app), `azd provision` (creates all Azure resources via Bicep), and `azd deploy` (uploads the zip to the Function App) in sequence. First-time provisioning takes 3–5 minutes.

When complete, the output shows:
```
Endpoint: https://<functionapp>.azurewebsites.net/
```

The MCP endpoint is at `https://<functionapp>.azurewebsites.net/mcp`.

### Code-only redeployment

After infrastructure is already provisioned, use the faster:

```bash
azd deploy
```

## Update mcp.json with the deployed URL

Open `.vscode/mcp.json` and replace the placeholder URL with the actual Function App hostname from the `azd up` output:

```json
"remote-mcp-server": {
    "type": "http",
    "url": "https://<functionapp>.azurewebsites.net/mcp"
}
```

## Known issues

### 1. Subscription ID vs. tenant ID confusion (MSA accounts)

For personal Microsoft accounts (hotmail, outlook, live), the tenant ID and subscription ID are two different GUIDs. Running `az account show` after login shows both: `tenantId` and `id` (subscription). Use `id` for `AZURE_SUBSCRIPTION_ID` and `tenantId` for the `--tenant-id` login flags. Using the wrong GUID causes `azd up` to fail with `failed to resolve user access to subscription`.

### 2. Install Azure CLI before running azd up

`azd` cannot resolve the current user's principal ID for personal Microsoft accounts using its own credential provider alone. Installing Azure CLI and running `az login` first lets `azd` fall back to the Azure CLI credential chain and succeed.


The Bicep parameter defaults to `''` and the deployment succeeds without it. The effect is that the deploying user does not get direct storage role assignments — this does not affect the MCP server's runtime behaviour.

## Connecting MCP clients to the deployed server

On first connection, the client detects a 401 from Easy Auth, opens a browser login for Entra, and caches the token. Subsequent connections are silent.

### GitHub Copilot (VS Code)

Uses `.vscode/mcp.json`. The `remote-mcp-server` entry is already configured. In VS Code, open the MCP panel or run **MCP: Connect to Server** → `remote-mcp-server`. GitHub Copilot Chat can then invoke the tools directly in agent mode (`@workspace` or `#tool`).

### Claude Code (VS Code extension)

Also uses `.vscode/mcp.json` — the same file as GitHub Copilot. The `remote-mcp-server` entry is already configured. You can add or update entries by editing the file directly, or by running `claude mcp add` from within Claude Code, which writes to `.vscode/mcp.json` automatically.

## GitHub Copilot vs. Claude Code (VS Code) — practical difference

| | GitHub Copilot | Claude Code (VS Code) |
|---|---|---|
| Config file | `.vscode/mcp.json` | `.vscode/mcp.json` (same file) |
| Where tools appear | Copilot Chat panel | Claude Code chat panel |
| Auth flow | VS Code handles browser popup | VS Code handles browser popup |
| Requires VS Code | Yes | Yes |

Both clients read the same `.vscode/mcp.json` and connect to the same `/mcp` endpoint. The only difference is which chat panel surfaces the tools.

## Tear down

```bash
azd down
```

Deletes all provisioned Azure resources including the resource group, Entra app registration, Managed Identity, storage account, and Function App.
