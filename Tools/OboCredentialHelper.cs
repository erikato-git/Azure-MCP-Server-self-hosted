using Azure.Identity;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace QuickstartWeatherServer.Tools;

internal static class OboCredentialHelper
{
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    internal static (OnBehalfOfCredential? Credential, string? ErrorJson) Create(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.Request.Headers == null)
            return (null, FailJson("No HTTP context or headers found.", null));

        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authHeaderValue) ||
            string.IsNullOrEmpty(authHeaderValue))
            return (null, FailJson(
                "No Authorization header found.",
                "Ensure you are authenticated via Azure App Service Easy Auth."));

        var authToken = authHeaderValue.ToString().Split(' ').LastOrDefault();
        if (string.IsNullOrEmpty(authToken))
            return (null, FailJson("Invalid Authorization header format.", null));

        var clientId = Environment.GetEnvironmentVariable("WEBSITE_AUTH_CLIENT_ID");
        var tenantId = Environment.GetEnvironmentVariable("WEBSITE_AUTH_AAD_ALLOWED_TENANTS");
        var federatedCredentialClientId = Environment.GetEnvironmentVariable("OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID");
        var tokenExchangeAudience = Environment.GetEnvironmentVariable("TokenExchangeAudience") ?? "api://AzureADTokenExchange";

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(federatedCredentialClientId))
            return (null, FailJson(
                "Missing required environment variables for Azure authentication. This tool only works when deployed to Azure.",
                "Check that WEBSITE_AUTH_CLIENT_ID, WEBSITE_AUTH_AAD_ALLOWED_TENANTS, and OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID are configured."));

        var managedIdentityCredential = new ManagedIdentityCredential(federatedCredentialClientId);
        var publicTokenExchangeScope = $"{tokenExchangeAudience}/.default";

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
