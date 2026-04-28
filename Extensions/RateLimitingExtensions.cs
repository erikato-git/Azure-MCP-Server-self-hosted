using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;

namespace QuickstartWeatherServer.Extensions;

public static class RateLimitingExtensions
{
    private const string PolicyName = "per-user";

    public static IServiceCollection AddMcpRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(PolicyName, context =>
            {
                var userId = context.User.FindFirst("oid")?.Value
                          ?? context.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                          ?? "anonymous";

                return RateLimitPartition.GetSlidingWindowLimiter(userId, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 4,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            });

            options.RejectionStatusCode = 429;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsync("Rate limit exceeded. Retry after 60 seconds.", ct);
            };
        });

        return services;
    }

    public static IEndpointConventionBuilder RequireMcpRateLimiting(this IEndpointConventionBuilder builder)
        => builder.RequireRateLimiting(PolicyName);
}
