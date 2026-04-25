# Azure MCP Server — Agent Instructions

## Project goal

Open-source template demonstrating how to host a self-managed Azure MCP server with custom tools that authenticate against the deployer's own Azure tenant. The server must run identically locally (for development) and in Azure (for production), and must be directly usable from Claude Code and GitHub Copilot as MCP clients.

## Architecture

| Layer | Choice | Rationale |
|---|---|---|
| MCP library | `ModelContextProtocol.AspNetCore` (Anthropic C# SDK) | Official SDK, portable, not Azure-specific |
| Hosting | Azure Functions (Flex Consumption) via custom handler | Serverless scale, serverless pricing, built-in Easy Auth |
| Transport | Stateless streamable-http on `/mcp` | Required for Azure Functions remote hosting |
| Auth | Azure App Service Easy Auth + OBO flow | Entra ID integration without custom auth code |
| Secrets (local) | `local.settings.json` / `.env` (never committed) | Standard Functions dev experience |
| Secrets (Azure) | Azure Key Vault (provisioned via Bicep) | All secrets and config referencing Key Vault |
| IaC | Bicep via `azd` | `azd up` single-command deploy |
| .NET version | .NET 8 (upgrade path to .NET 9 / .NET 10 planned) | Current LTS, in-place upgrade is low-risk |

## Tool authoring pattern

All tools live in [Tools/](Tools/). A tool class looks like:

```csharp
[McpServerToolType]
public sealed class MyTools
{
    [McpServerTool, Description("One-line description the AI model reads.")]
    public static async Task<string> DoSomething(
        IHttpContextAccessor httpContextAccessor,
        [Description("Parameter description.")] string input)
    {
        // ...
        return result;
    }
}
```

Register it in [Program.cs](Program.cs):
```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(...)
    .WithTools<MyTools>();  // ← add here
```

Rules:
- One class per domain area (e.g., `ArmTools.cs`, `KeyVaultTools.cs`)
- Always return `string` (JSON-serialized for structured data)
- Tools that require Azure auth must work only when deployed (document this clearly)
- Tools that work locally must not hard-require deployed infrastructure

## Authentication model (OBO flow)

1. MCP client (Claude Code / GitHub Copilot) connects to the deployed Function App
2. Easy Auth returns a 401 with PRM metadata — the client initiates Entra login
3. The user authenticates with their own Entra ID tenant
4. Easy Auth validates the token and forwards it as `Authorization: Bearer <token>` header
5. MCP tools extract the bearer token from `IHttpContextAccessor` and exchange it for a downstream token via `OnBehalfOfCredential` (using the Managed Identity as the federated credential)
6. The tool calls the downstream API (ARM, Key Vault, Graph, etc.) with the exchanged token

See [Tools/UserInfoTools.cs](Tools/UserInfoTools.cs) for the reference implementation of this pattern.

Key environment variables set automatically by Easy Auth and the infra:
- `WEBSITE_AUTH_CLIENT_ID` — Entra app client ID
- `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` — tenant ID
- `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` — Managed Identity client ID for FIC assertion

## Secret management

### Local development
Secrets go in `local.settings.json` (gitignored) or a `.env` file (gitignored). Never commit either file.

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsFeatureFlags": "EnableMcpCustomHandlerPreview",
    "MY_SECRET": "value-for-local-dev"
  }
}
```

For local OBO testing, `DefaultAzureCredential` falls back to the developer's Azure CLI login — run `az login` before starting the server.

### Azure (production)
All secrets are stored in the Key Vault provisioned by [infra/main.bicep](infra/main.bicep). The Function App accesses them via Key Vault references in app settings:

```bicep
MY_SECRET: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=my-secret)'
```

The Function App's Managed Identity is granted `Key Vault Secrets User` role on the vault.

## Infrastructure (Bicep / azd)

Key Bicep modules:
- `infra/main.bicep` — root, subscription scope, creates resource group
- `infra/app/entra.bicep` — Entra app registration (auto-created)
- `infra/app/mcp.bicep` — Function App + Easy Auth
- `infra/app/rbac.bicep` — RBAC assignments for Managed Identity

To add Key Vault for a new secret:
1. Add the Key Vault module to `infra/main.bicep`
2. Add `Key Vault Secrets User` RBAC assignment in `infra/app/rbac.bicep`
3. Reference the secret in the `appSettings` block of `infra/app/mcp.bicep` using the Key Vault reference syntax

## Development workflow

```bash
# Start locally
func start

# Deploy to Azure (first time or after infra changes)
azd up

# Deploy after code-only changes
azd deploy

# Tear down Azure resources
azd down
```

Pre-authorize Claude Code as MCP client (VS Code client ID):
```bash
azd env set PRE_AUTHORIZED_CLIENT_IDS aebc6443-996d-45c2-90f0-388ff96faa56
```

Register the `Microsoft.App` resource provider before first deploy:
```bash
az provider register --namespace 'Microsoft.App'
```

## Planned work (not yet implemented)

- **Azure Key Vault Bicep module** — provision Key Vault, wire RBAC, add Key Vault references in app settings
- **ArmTools.cs** — tools for Azure Resource Manager (list resource groups, list resources, etc.)
- **KeyVaultTools.cs** — tools for Key Vault operations (list/get secrets, keys, certs)
- **GitHub Actions CI/CD** — `azd pipeline config` + workflow for automated deploy on push to main
- **.NET 9 / .NET 10 upgrade** — update `TargetFramework` in `.csproj` and `runtimeVersion` in Bicep when ready

## File map

```
Program.cs                  — app entry point, MCP server setup, DI registration
Tools/
  WeatherTools.cs           — sample: stateless tool, no auth required
  UserInfoTools.cs          — reference: OBO flow to call Microsoft Graph
  HttpClientExt.cs          — helper: typed JSON reads from HttpClient
infra/
  main.bicep                — root Bicep template (subscription scope)
  app/entra.bicep           — Entra app registration
  app/mcp.bicep             — Function App + Easy Auth config
  app/rbac.bicep            — RBAC role assignments
  app/vnet.bicep            — VNet + private endpoints
  app/storage-PrivateEndpoint.bicep
  abbreviations.json        — Azure resource name abbreviations
host.json                   — Functions host config (custom handler port + path)
local.settings.json         — local secrets and env vars (gitignored)
azure.yaml                  — azd service definition
.vscode/mcp.json            — MCP server registrations for VS Code
```

## Coding conventions

- No comments unless the *why* is non-obvious
- Return `string` from all MCP tools; use `System.Text.Json` for structured output
- `JsonSerializerOptions { WriteIndented = true }` for human-readable tool responses
- Mask PII in tool responses (see `UserInfoTools.cs` for the pattern)
- Inject dependencies via method parameters (Functions DI style), not constructor injection
- Tools that require deployed infrastructure must return a clear error message when run locally
