---
applyTo: "**"
---

# Calling GetCurrentUser on remote-mcp-server

The `GetCurrentUser` tool retrieves authenticated user information from Microsoft Graph using Azure App Service Easy Auth and the On-Behalf-Of (OBO) credential flow. It works only when deployed to Azure.

## Prerequisites

- Remote MCP server deployed to Azure (via `azd up`)
- `.vscode/mcp.json` configured with `remote-mcp-server` pointing to your Function App
- Azure CLI (`az`) installed and authenticated

## Authentication flow

1. **Azure CLI login** — Authenticate with your tenant and grant consent to the MCP server app scope
2. **Token retrieval** — Obtain an access token scoped to the remote MCP server
3. **MCP call** — Invoke `GetCurrentUser` with the bearer token in the Authorization header
4. **Easy Auth validation** — The Function App's Easy Auth layer validates the token and forwards it as a header
5. **OBO exchange** — `GetCurrentUser` exchanges the user token for a downstream token via `OnBehalfOfCredential`
6. **Graph API call** — The tool calls Microsoft Graph with the downstream token to retrieve user information

## Getting started

### Step 1: Authenticate with Azure CLI (first time only)

```bash
az login --tenant "<your-tenant-id>" --scope "api://<function-app-id-uri>/.default"
```

This opens a browser for interactive authentication and consent. You must grant permission for the Azure CLI to access the MCP server's resource.

**Note on consent:** If you see `AADSTS65001: The user or administrator has not consented`, this is expected. Complete the browser login to consent.

### Step 2: Call GetCurrentUser via the MCP tool

In VS Code, use the MCP tool interface to call `GetCurrentUser`:

- Open the MCP panel or run **MCP: Connect to Server** → `remote-mcp-server`
- Invoke the `GetCurrentUser` tool
- The tool returns your authenticated user information from Microsoft Graph

### Alternative: Manual curl call

```bash
# Get an access token
$token = az account get-access-token --resource "api://<function-app-id-uri>" --query accessToken -o tsv

# Call the MCP endpoint with the bearer token
curl -X POST "https://<function-app>.azurewebsites.net/mcp" \
  -H "Authorization: Bearer $token" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc":"2.0",
    "id":1,
    "method":"tools/call",
    "params":{
      "name":"GetCurrentUser",
      "arguments":{}
    }
  }'
```

## Expected response

On success:

```json
{
  "authenticated": true,
  "user": {
    "displayName": "Your Name",
    "givenName": "First",
    "surname": "Last",
    "userPrincipalName": "your.email@tenant.onmicrosoft.com",
    "mail": "[MASKED]",
    "id": "[MASKED]",
    "businessPhones": []
  },
  "message": "Successfully retrieved user information from Microsoft Graph"
}
```

## Calling from Claude Code (AI assistant)

Asking Claude Code to call `GetCurrentUser` directly via its Bash tool or `!`-prefixed shell commands **will not work**. The reasons:

1. **Headless shell** — `!` commands in Claude Code run in a shell with no display. `az login` (browser-based) and `--use-device-code` (device code shown in terminal) both fail silently — the browser never opens and no device code appears in the conversation.

2. **Cached consent error** — After a failed token attempt, MSAL caches the `AADSTS65001: consent_required` error locally. Subsequent `az account get-access-token` calls return the same cached error without hitting AAD, even if the underlying issue has changed.

3. **Easy Auth blocks unauthenticated requests** — Without a valid Bearer token, the MCP endpoint returns `HTTP 401` immediately. There is no way to bypass this from a headless context.

**What works:** Use VS Code's built-in MCP client (`.vscode/mcp.json` → `remote-mcp-server`). VS Code handles the Easy Auth OAuth flow with a proper browser popup, obtains the token on your behalf, and sends it with every MCP request. Ask Claude Code to call `GetCurrentUser` *after* VS Code has established an authenticated MCP session.

## Troubleshooting

### "No authorization header found"

The request reached the MCP server without an Authorization header. Ensure:
1. You're calling the remote server (not `localhost:8080`)
2. You've included the `Authorization: Bearer <token>` header
3. The token is still valid (tokens expire after ~1 hour)

### "OnBehalfOfCredential authentication failed"

The OBO credential could not exchange your token for a downstream Graph token. This usually means:
1. You haven't granted consent to the application scope
2. The Managed Identity is not properly configured on the Function App
3. The environment variables (`WEBSITE_AUTH_CLIENT_ID`, `WEBSITE_AUTH_AAD_ALLOWED_TENANTS`, `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID`) are missing

**Solution:** Re-authenticate and grant consent:
```bash
az logout
az login --tenant "<your-tenant-id>" --scope "api://<function-app-id-uri>/.default"
```

### "Missing required environment variables"

The Function App is missing critical configuration. Re-deploy:
```bash
azd up
```

## Local development

The `GetCurrentUser` tool **does not work locally** because Easy Auth only runs on Azure. Testing requires deployment to Azure.

To test locally, use the `GetAlerts` or `GetForecast` tools instead — they require no authentication and work on both `http://localhost:8080` and the deployed Function App.
