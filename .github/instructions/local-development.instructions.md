---
applyTo: "**"
---

# Local development workflow

## Why `dotnet run`, not `func start`

The MCP server is a plain ASP.NET Core app that owns the MCP protocol layer directly via `ModelContextProtocol.AspNetCore`. It listens on port 8080 and exposes `/mcp` itself — Azure Functions Core Tools (`func`) is only the production hosting shell that adds Easy Auth routing on top. For local development, `func` adds no value and requires the compiled binary to be in the project root (which conflicts with the normal build output path). Run the app directly instead.

## Prerequisites

- .NET 8 SDK
- `az login` — only needed if testing OBO-based tools against a deployed instance; not required for local WeatherTools calls

## Starting the server

```bash
dotnet run --project QuickstartWeatherServer.csproj
```

The server starts on `http://0.0.0.0:8080`. The MCP endpoint is at `http://localhost:8080/mcp`.

## Connecting Claude Code as MCP client

The `.vscode/mcp.json` `local-mcp-server` entry points to `http://localhost:8080/mcp`. Start the server first, then connect via the MCP panel in VS Code or the command palette (`MCP: Connect to Server` → `local-mcp-server`).

## Verifying the endpoint manually

Health check:
```bash
curl http://localhost:8080/api/healthz
# → Healthy
```

MCP handshake:
```bash
curl -s -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'
```

Call `GetAlerts` (no auth required):
```bash
curl -s -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"GetAlerts","arguments":{"state":"CA"}}}'
```

Call `GetCurrentUser` (expected failure locally):
```bash
curl -s -X POST http://localhost:8080/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"GetCurrentUser","arguments":{}}}'
# → {"authenticated":false,"message":"No authorization header found"}
```

## Expected tool behaviour locally

| Tool | Local result |
|---|---|
| `GetAlerts` | Live data from weather.gov — works fully |
| `GetForecast` | Live data from weather.gov — works fully |
| `GetCurrentUser` | `"No authorization header found"` — correct; Easy Auth only runs in Azure |

Tools that require Easy Auth (`GetCurrentUser` and any future OBO-based tools) must be tested against a deployed Function App.
