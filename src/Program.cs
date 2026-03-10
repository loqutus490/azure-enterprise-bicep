using Azure.AI.OpenAI;
using Azure.Identity;
using LegalRagApp.Middleware;
using LegalRagApp.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
});

// Default to true so that tests and deployments without explicit toggles still exercise auth.
var enableAzureAd = builder.Configuration.GetValue<bool?>("Authorization:EnableAzureAd") ?? true;
if (enableAzureAd)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
}

var requiredApiRole = builder.Configuration["Authorization:RequiredRole"] ?? "Api.Access";
var requiredScope = builder.Configuration["Authorization:RequiredScope"] ?? "access_as_user";
var allowedClientAppIds = builder.Configuration.GetSection("Authorization:AllowedClientAppIds").Get<string[]>() ?? Array.Empty<string>();
var allowedClientAppIdSet = new HashSet<string>(
    allowedClientAppIds.Where(clientAppId => !string.IsNullOrWhiteSpace(clientAppId)),
    StringComparer.OrdinalIgnoreCase);
var corsAllowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (corsAllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsAllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiAccessPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var user = context.User;
            var appId = user.FindFirst("azp")?.Value ?? user.FindFirst("appid")?.Value;
            var scopeClaim = user.FindFirst("scp")?.Value
                ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;

            if (!string.IsNullOrWhiteSpace(scopeClaim))
            {
                var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hasRequiredScope = scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
                if (!hasRequiredScope)
                    return false;

                if (allowedClientAppIdSet.Count == 0)
                    return true;

                return !string.IsNullOrWhiteSpace(appId) && allowedClientAppIdSet.Contains(appId);
            }

            if (string.IsNullOrWhiteSpace(appId))
                return false;

            var idType = user.FindFirst("idtyp")?.Value;
            if (!string.IsNullOrWhiteSpace(idType) && !string.Equals(idType, "app", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!user.IsInRole(requiredApiRole))
                return false;

            if (allowedClientAppIdSet.Count == 0)
                return true;

            return allowedClientAppIdSet.Contains(appId);
        });
    });
});

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpointValue = config["AzureOpenAI:Endpoint"];
    if (string.IsNullOrWhiteSpace(endpointValue))
        throw new InvalidOperationException("Missing configuration: AzureOpenAI:Endpoint");

    return new AzureOpenAIClient(new Uri(endpointValue), new DefaultAzureCredential());
});

builder.Services.AddSingleton<IIndexVersionService, IndexVersionService>();
builder.Services.AddSingleton<IAuthorizationFilter, AuthorizationFilter>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();
builder.Services.AddSingleton<IRetrievalService, RetrievalService>();
builder.Services.AddSingleton<IChatService, ChatService>();
builder.Services.AddSingleton<IPromptSecurityService, PromptSecurityService>();
builder.Services.AddSingleton<IQueryRewriteService, QueryRewriteService>();
builder.Services.AddSingleton<IConfidenceService, ConfidenceService>();
builder.Services.AddSingleton<IMetricsService, MetricsService>();
builder.Services.AddSingleton<IProvenanceService, ProvenanceService>();
builder.Services.AddSingleton<ILineageService, LineageService>();
builder.Services.AddSingleton<IReindexService, ReindexService>();
builder.Services.AddSingleton<IHealthCheckService, HealthCheckService>();
builder.Services.AddSingleton<IAuditService, AuditService>();

var memoryProvider = builder.Configuration["Memory:Provider"] ?? "InMemory";
if (string.Equals(memoryProvider, "Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisConnectionString = builder.Configuration["Memory:Redis:ConnectionString"];
    if (string.IsNullOrWhiteSpace(redisConnectionString))
    {
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<IMemoryService, InMemoryMemoryService>();
    }
    else
    {
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = builder.Configuration["Memory:Redis:InstanceName"] ?? "legalrag";
        });
        builder.Services.AddSingleton<IMemoryService, RedisMemoryService>();
    }
}
else
{
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSingleton<IMemoryService, InMemoryMemoryService>();
}

builder.Services.AddControllers();

var app = builder.Build();
var logger = app.Logger;
var bypassAuthInDevelopment = app.Environment.IsDevelopment()
    && app.Configuration.GetValue<bool>("Authorization:BypassAuthInDevelopment");
var bypassMatterAuthorizationInDevelopment = app.Environment.IsDevelopment()
    && app.Configuration.GetValue<bool>("Authorization:BypassMatterAuthorizationInDevelopment");

if (allowedClientAppIdSet.Count == 0 && !app.Environment.IsDevelopment())
{
    logger.LogWarning("Authorization:AllowedClientAppIds is empty outside Development. Any caller app with required scope/role may be allowed.");
}

if (bypassAuthInDevelopment)
{
    logger.LogWarning("Authorization bypass is enabled for Development. Do not enable this outside local debugging.");
}

if (bypassMatterAuthorizationInDevelopment)
{
    logger.LogWarning("Matter-level claim authorization bypass is enabled for Development. Do not enable this outside local debugging.");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<PromptSecurityMiddleware>();
app.UseMiddleware<AuditLoggingMiddleware>();
app.UseMiddleware<CostTrackingMiddleware>();

app.MapGet("/ping", () => "pong");

var versionEndpoint = app.MapGet("/version", () => "SECURED_BUILD_V2");
if (!bypassAuthInDevelopment)
{
    versionEndpoint.RequireAuthorization("ApiAccessPolicy");
}

app.MapControllers();

app.Run();

public partial class Program { }
