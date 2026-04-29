# Application Insights Reporting Tool — End-to-End Flow

This document traces the journey of the example user prompt:

> *"Give me a summary of the events for the last 24 hours."*

from the moment the MCP client receives it until a JSON report is rendered back to the user. It also documents the OBO (On-Behalf-Of) authentication exchange and the Bicep resources that make the whole thing work.

Each step carries a stable ID (`[FLOW-NN]`, `[OBO-NN]`, `[BICEP-NN]`). The same IDs exist as inline comments in the source/Bicep files so you can jump from this report directly to the implementing line in VS Code.

---

## 1. Overview

`ApplicationInsightsReportingTools.GetApplicationInsightsReport` is an MCP tool that returns an aggregated report from an Application Insights resource. It accepts a resource group name, a data type (`requests`, `exceptions`, `dependencies`, `traces`, `customEvents`, `availabilityResults`, `pageViews`, or `all`), and a time range. It auto-discovers the subscription and the AI component, then runs one KQL query per data type against the linked Log Analytics workspace and returns a single JSON document.

End-to-end, the path is:

```
User prompt
  -> MCP client (Claude Code / Copilot via VS Code)
  -> Easy Auth (Entra login + 401 challenge)
  -> Function App custom handler (/mcp)
  -> MCP SDK dispatch -> tool method
  -> OBO token exchange (user JWT -> ARM token, via MI federated credential)
  -> ARM: discover sub, AI component, linked Log Analytics workspace
  -> Log Analytics: KQL query per data type
  -> Aggregated JSON returned to the model
  -> Model summarises in natural language
```

---

## 2. Event flow

| ID | Step | Where |
|---|---|---|
| `[FLOW-01]` | The user has registered the deployed MCP server in their VS Code MCP config. The model sees the tool catalog, picks `GetApplicationInsightsReport`, and the host opens an HTTP request to `https://<func>.azurewebsites.net/mcp`. | [.vscode/mcp.json:9](../.vscode/mcp.json#L9) |
| `[FLOW-02]` | First call has no token. Easy Auth (configured by `[BICEP-06]`) responds **401** with PRM metadata pointing at the Entra tenant. The MCP client opens the browser, the user signs in, Easy Auth validates the JWT and persists the session. | [infra/app/mcp.bicep:144](../infra/app/mcp.bicep#L144) |
| `[FLOW-03]` | On the next call Easy Auth attaches the validated user JWT as `Authorization: Bearer <token>` and forwards the request to the custom handler on port `8080`. | [infra/app/mcp.bicep:144](../infra/app/mcp.bicep#L144) (auth resource) + Easy Auth runtime |
| `[FLOW-04]` | The Functions runtime forwards the HTTP request to the .NET custom handler (`dotnet QuickstartWeatherServer.dll`) configured in `host.json`. The ASP.NET pipeline routes `/mcp` to the MCP SDK via `app.MapMcp("/mcp")` in `Program.cs`. | [host.json:4](../host.json#L4) + [Program.cs:68](../Program.cs#L68) |
| `[FLOW-05]` | The MCP SDK deserializes tool arguments (`resourceGroupName`, `dataType="all"`, `timeRangeValue=24`, `timeRangeUnit="hours"`) and invokes the `[McpServerTool]`-decorated method. | [Tools/ApplicationInsightsReportingTools.cs:20](../Tools/ApplicationInsightsReportingTools.cs#L20) |
| `[FLOW-06]` | Validate `dataType` against the allow-list — fail fast before doing any auth or network work. | [Tools/ApplicationInsightsReportingTools.cs:31](../Tools/ApplicationInsightsReportingTools.cs#L31) |
| `[FLOW-07]` | Normalize and validate `timeRangeUnit` (`hours` / `days`). | [Tools/ApplicationInsightsReportingTools.cs:37](../Tools/ApplicationInsightsReportingTools.cs#L37) |
| `[FLOW-08]` | Build an `OnBehalfOfCredential` from the inbound user bearer token. The factory pulls the token off `IHttpContextAccessor`, reads the Easy-Auth env vars, and wires up the federated assertion callback. See section 3 for the OBO deep-dive. | [Tools/ApplicationInsightsReportingTools.cs:44](../Tools/ApplicationInsightsReportingTools.cs#L44) + [Helpers/OboCredentialHelper.cs:11](../Helpers/OboCredentialHelper.cs#L11) |
| `[FLOW-09]` | Force the OBO exchange by requesting an ARM-scoped token (`https://management.azure.com/.default`). This is when MSAL actually calls the v2 token endpoint with the user assertion + the MI-issued client assertion. | [Tools/ApplicationInsightsReportingTools.cs:55](../Tools/ApplicationInsightsReportingTools.cs#L55) |
| `[FLOW-10]` | If the user did not pass `subscriptionId`, list the subscriptions the user can see and probe each one for the named resource group. | [Tools/ApplicationInsightsReportingTools.cs:64](../Tools/ApplicationInsightsReportingTools.cs#L64) |
| `[FLOW-11]` | Use `ArmClient` + the OBO credential to enumerate `microsoft.insights/components` inside the resource group. If exactly one matches, proceed; otherwise return a structured error/disambiguation JSON. | [Tools/ApplicationInsightsReportingTools.cs:81](../Tools/ApplicationInsightsReportingTools.cs#L81) |
| `[FLOW-12]` | Read the AI component's `properties.WorkspaceResourceId` to find the linked Log Analytics workspace — KQL queries hit the workspace, not the AI component. | [Tools/ApplicationInsightsReportingTools.cs:115](../Tools/ApplicationInsightsReportingTools.cs#L115) |
| `[FLOW-13]` | For `dataType="all"`, expand to all 7 concrete types (`requests`, `exceptions`, `dependencies`, `traces`, `customEvents`, `availabilityResults`, `pageViews`) and run one query per type. | [Tools/ApplicationInsightsReportingTools.cs:129](../Tools/ApplicationInsightsReportingTools.cs#L129) |
| `[FLOW-14]` | `RunKql` POSTs `{ query, timespan: "PT24H" }` to `https://management.azure.com/<workspaceResourceId>/api/query?api-version=2020-08-01` with the OBO bearer. The user must hold **Monitoring Reader** (or equivalent) on the workspace — this RBAC is *not* in Bicep, it's the deployer's pre-existing access. | [Tools/ApplicationInsightsReportingTools.cs:178](../Tools/ApplicationInsightsReportingTools.cs#L178) |
| `[FLOW-15]` | Compose the per-type aggregates into a single object and serialise with `WriteIndented = true`. The MCP SDK ships the string back over the streamable-HTTP transport, the model summarises it in natural language for the user. | [Tools/ApplicationInsightsReportingTools.cs:134](../Tools/ApplicationInsightsReportingTools.cs#L134) |

---

## 3. OBO authentication deep-dive

Three actors are involved. Keep them straight or the rest is gibberish:

| Actor | Identity | Role |
|---|---|---|
| **User** | Entra user in the deployer's tenant | Holds the original interactive session token. Owns the downstream RBAC (e.g. Monitoring Reader on App Insights). |
| **Entra app registration** | `[BICEP-02]` | The OAuth2 audience and `client_id` for both Easy Auth and the OBO call. Has no client secret. |
| **User-assigned Managed Identity** | `[BICEP-01]` | Stands in for the missing client secret. Its tokens (audience `api://AzureADTokenExchange`) are accepted by Entra as a Federated Identity Credential proof for the Entra app. |

The OBO exchange itself, in code, is five steps inside `OboCredentialHelper.Create`:

| ID | Step | Line |
|---|---|---|
| `[OBO-01]` | Read the user's JWT from the inbound `Authorization` header. Easy Auth has already validated it, so we can trust it without re-checking the signature. | [Helpers/OboCredentialHelper.cs:19](../Helpers/OboCredentialHelper.cs#L19) |
| `[OBO-02]` | Strip the `Bearer ` prefix and keep just the raw JWT — that's the **user assertion** that goes into the OBO request body. | [Helpers/OboCredentialHelper.cs:26](../Helpers/OboCredentialHelper.cs#L26) |
| `[OBO-03]` | Read three env vars set by infra: `WEBSITE_AUTH_CLIENT_ID` (the Entra app id), `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` (the tenant id), `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` (the MI client id used as the FIC subject). See `[BICEP-08]`. | [Helpers/OboCredentialHelper.cs:31](../Helpers/OboCredentialHelper.cs#L31) |
| `[OBO-04]` | Construct a `ManagedIdentityCredential` for the FIC client id and request a token for `api://AzureADTokenExchange/.default`. This token is the **client assertion** that proves to Entra "I am the Entra app" — replacing what would normally be `client_secret`. | [Helpers/OboCredentialHelper.cs:42](../Helpers/OboCredentialHelper.cs#L42) |
| `[OBO-05]` | Build an `OnBehalfOfCredential` with `(tenantId, clientId, assertionCallback, userAssertion)`. The callback is invoked lazily by MSAL each time the credential is asked for a downstream token, so the MI assertion stays fresh. | [Helpers/OboCredentialHelper.cs:46](../Helpers/OboCredentialHelper.cs#L46) |

When `[FLOW-09]` calls `credential.GetTokenAsync(...)`, MSAL performs:

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
  grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
  client_id=<Entra app client id>           # WEBSITE_AUTH_CLIENT_ID
  client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
  client_assertion=<MI-issued JWT>           # from [OBO-04]
  assertion=<user JWT>                       # from [OBO-02]
  scope=https://management.azure.com/.default
  requested_token_use=on_behalf_of
```

Entra validates the MI JWT against the Federated Identity Credential (`[BICEP-05]`), confirms the user JWT is for this app, and issues an ARM token whose subject is the *user*, not the MI. That token is what the tool uses for every downstream ARM and Log Analytics call — so all the tenant's existing user RBAC continues to apply.

### Why the FIC dance at all

A Functions custom handler running in Flex Consumption has no place to securely store an Entra app client secret. Federated Identity Credentials let the Managed Identity *be* the proof — no secret rotation, no Key Vault round-trip, and Easy Auth's own `clientSecretSettingName` (`[BICEP-07]`) hooks into the same MI clientId env var so even Easy Auth's token endpoint calls go via FIC.

---

## 4. Infrastructure (Bicep)

| ID | Resource / setting | File |
|---|---|---|
| `[BICEP-01]` | User-assigned **Managed Identity** — federated credential subject for the Entra app and the principal under which the host process runs (storage, telemetry export). | [infra/main.bicep:90](../infra/main.bicep#L90) |
| `[BICEP-02]` | **Entra app registration** — issuer of the OBO-eligible audience; exposes `user_impersonation` scope. `requestedAccessTokenVersion: 2` is required for OBO. | [infra/app/entra.bicep:88](../infra/app/entra.bicep#L88) |
| `[BICEP-03]` | **`requiredResourceAccess`** — declares delegated downstream APIs (Graph `User.Read` shown). ARM is *not* declared here because ARM grants are dynamic; the user's existing RBAC carries the call. | [infra/app/entra.bicep:109](../infra/app/entra.bicep#L109) |
| `[BICEP-04]` | **`preAuthorizedApplications`** — well-known client IDs (e.g. the VS Code first-party app `aebc6443-996d-45c2-90f0-388ff96faa56`, used by both Claude Code and GitHub Copilot) that skip the admin-consent prompt for the default scope. | [infra/app/entra.bicep:67](../infra/app/entra.bicep#L67) |
| `[BICEP-05]` | **Federated Identity Credential** — links the MI's principal id to the Entra app, with audience `api://AzureADTokenExchange`. This is the linchpin: without it, no MI token can stand in as the app's client credential. | [infra/app/entra.bicep:131](../infra/app/entra.bicep#L131) |
| `[BICEP-06]` | **Easy Auth (App Service Authentication v2)** — `requireAuthentication: true`, `unauthenticatedClientAction: 'Return401'`. This produces the 401-with-PRM challenge in `[FLOW-02]` and validates the JWT before forwarding it to the custom handler in `[FLOW-03]`. | [infra/app/mcp.bicep:144](../infra/app/mcp.bicep#L144) |
| `[BICEP-07]` | **`clientSecretSettingName: 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'`** — the magic flag that tells Easy Auth to treat the named app setting as a Managed-Identity client id for FIC, instead of looking up a secret value. | [infra/app/mcp.bicep:172](../infra/app/mcp.bicep#L172) |
| `[BICEP-08]` | **Auth app settings** — `WEBSITE_AUTH_AAD_ALLOWED_TENANTS` (tenant id), `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` (MI client id). Easy Auth also injects `WEBSITE_AUTH_CLIENT_ID` automatically. These are the env vars `OboCredentialHelper` reads in `[OBO-03]`. | [infra/app/mcp.bicep:68](../infra/app/mcp.bicep#L68) |
| `[BICEP-09]` | **Log Analytics workspace** — backing store for AI telemetry and the actual KQL query target (`AppRequests`, `AppExceptions`, …). | [infra/main.bicep:245](../infra/main.bicep#L245) |
| `[BICEP-10]` | **Application Insights component** — linked to the workspace (`workspaceResourceId`). `disableLocalAuth: true` forces AAD-authenticated ingestion only. | [infra/main.bicep:258](../infra/main.bicep#L258) |
| `[BICEP-11]` | **Monitoring Metrics Publisher** RBAC for the MI on App Insights — used by the host's OTel exporter. **Note:** this is for *writing* telemetry as the MI. The *read* side that powers the report (`/api/query`) runs as the **end user** via OBO, and the user must already hold a role like Monitoring Reader on the workspace. That RBAC is intentionally not in Bicep — it's the deployer's existing tenant access. | [infra/app/rbac.bicep:90](../infra/app/rbac.bicep#L90) |

### Resources intentionally omitted

- `infra/app/vnet.bicep` and `infra/app/storage-PrivateEndpoint.bicep` — they harden network egress for the Function App backplane but do not participate in the MCP request/auth/query path.
- `Program.cs` — the OTel/MCP setup is referenced from `[FLOW-04]` for context; per project convention no anchor comments were added there.

---

## 5. Cross-reference table

| ID | File | Line |
|---|---|---|
| FLOW-01 | `.vscode/mcp.json` | 9 |
| FLOW-02 | `infra/app/mcp.bicep` | 145 |
| FLOW-03 | `infra/app/mcp.bicep` | 145 |
| FLOW-04 | `host.json` + `Program.cs` | 4 / 68 |
| FLOW-05 | `Tools/ApplicationInsightsReportingTools.cs` | 20 |
| FLOW-06 | `Tools/ApplicationInsightsReportingTools.cs` | 31 |
| FLOW-07 | `Tools/ApplicationInsightsReportingTools.cs` | 37 |
| FLOW-08 | `Tools/ApplicationInsightsReportingTools.cs` + `Helpers/OboCredentialHelper.cs` | 44 / 11 |
| FLOW-09 | `Tools/ApplicationInsightsReportingTools.cs` | 55 |
| FLOW-10 | `Tools/ApplicationInsightsReportingTools.cs` | 64 |
| FLOW-11 | `Tools/ApplicationInsightsReportingTools.cs` | 81 |
| FLOW-12 | `Tools/ApplicationInsightsReportingTools.cs` | 115 |
| FLOW-13 | `Tools/ApplicationInsightsReportingTools.cs` | 129 |
| FLOW-14 | `Tools/ApplicationInsightsReportingTools.cs` | 178 |
| FLOW-15 | `Tools/ApplicationInsightsReportingTools.cs` | 134 |
| OBO-01 | `Helpers/OboCredentialHelper.cs` | 19 |
| OBO-02 | `Helpers/OboCredentialHelper.cs` | 26 |
| OBO-03 | `Helpers/OboCredentialHelper.cs` | 31 |
| OBO-04 | `Helpers/OboCredentialHelper.cs` | 42 |
| OBO-05 | `Helpers/OboCredentialHelper.cs` | 46 |
| BICEP-01 | `infra/main.bicep` | 90 |
| BICEP-02 | `infra/app/entra.bicep` | 88 |
| BICEP-03 | `infra/app/entra.bicep` | 109 |
| BICEP-04 | `infra/app/entra.bicep` | 67 |
| BICEP-05 | `infra/app/entra.bicep` | 131 |
| BICEP-06 | `infra/app/mcp.bicep` | 144 |
| BICEP-07 | `infra/app/mcp.bicep` | 172 |
| BICEP-08 | `infra/app/mcp.bicep` | 68 |
| BICEP-09 | `infra/main.bicep` | 245 |
| BICEP-10 | `infra/main.bicep` | 258 |
| BICEP-11 | `infra/app/rbac.bicep` | 90 |
