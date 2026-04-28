using Azure.Identity;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace QuickstartWeatherServer.Helpers;

internal static class OboCredentialHelper
{
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    // [FLOW-08] Called by tools to wrap the inbound user token in an OnBehalfOfCredential.
    internal static (OnBehalfOfCredential? Credential, string? ErrorJson) Create(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.Request.Headers == null)
            return (null, FailJson("No HTTP context or headers found.", null));

        // [OBO-01] Easy Auth forwards the validated user JWT as Authorization: Bearer <token>.
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authHeaderValue) ||
            string.IsNullOrEmpty(authHeaderValue))
            return (null, FailJson(
                "No Authorization header found.",
                "Ensure you are authenticated via Azure App Service Easy Auth."));

        // [OBO-02] Strip the "Bearer " prefix, keeping just the raw JWT for the OBO assertion.
        var authToken = authHeaderValue.ToString().Split(' ').LastOrDefault();
        if (string.IsNullOrEmpty(authToken))
            return (null, FailJson("Invalid Authorization header format.", null));

        // [OBO-03] Read Easy-Auth and FIC env vars injected by infra (see BICEP-08).
        var clientId = Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID");
        var tenantId = Environment.GetEnvironmentVariable("WEBSITE_AUTH_AAD_ALLOWED_TENANTS");
        var federatedCredentialClientId = Environment.GetEnvironmentVariable("OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID");
        var tokenExchangeAudience = Environment.GetEnvironmentVariable("TokenExchangeAudience") ?? "api://AzureADTokenExchange";

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(federatedCredentialClientId))
            return (null, FailJson(
                "Missing required environment variables for Azure authentication. This tool only works when deployed to Azure.",
                "Check that WEBSITE_AUTH_CLIENT_ID, WEBSITE_AUTH_AAD_ALLOWED_TENANTS, and OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID are configured."));

        // [OBO-04] MI token (audience api://AzureADTokenExchange) replaces the Entra app's client secret.
        var managedIdentityCredential = new ManagedIdentityCredential(federatedCredentialClientId);
        var publicTokenExchangeScope = $"{tokenExchangeAudience}/.default";

        // [OBO-05] OnBehalfOfCredential: assertion callback fires on each downstream token request and supplies a fresh MI-issued FIC token.
        var credential = new OnBehalfOfCredential(
            tenantId,
            clientId,
            async (cancellationToken) =>
            {
                var token = await managedIdentityCredential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext([publicTokenExchangeScope]),
                    cancellationToken);
                return token.Token;
            },
            authToken);

        return (credential, null);
    }

    internal static string FailJson(string message, string? hint) =>
        JsonSerializer.Serialize(new
        {
            error = true,
            message,
            hint = hint ?? "This tool requires deployment to Azure with Easy Auth configured."
        }, JsonOptions);

    internal static string ErrorJson(string message, string? hint, object? context = null) =>
        JsonSerializer.Serialize(new
        {
            error = true,
            message,
            hint = hint ?? "Check your Azure permissions and try again.",
            context
        }, JsonOptions);
}
