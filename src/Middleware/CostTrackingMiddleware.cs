using LegalRagApp.Models;
using Microsoft.Extensions.Logging;

namespace LegalRagApp.Middleware;

// Logs token usage and estimated cost for each /ask request.
public sealed class CostTrackingMiddleware
{
    private readonly RequestDelegate _next;

    public CostTrackingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ILogger<CostTrackingMiddleware> logger)
    {
        await _next(context);

        if (!context.Request.Path.Equals("/ask", StringComparison.OrdinalIgnoreCase))
            return;

        if (context.Items.TryGetValue(AuditLoggingMiddleware.AuditRecordItemKey, out var value)
            && value is AskAuditRecord record)
        {
            logger.LogInformation(
                "COST_AI_QUERY user={UserId} promptTokens={PromptTokens} completionTokens={CompletionTokens} estimatedCost={EstimatedCost}",
                record.UserId,
                record.PromptTokens,
                record.CompletionTokens,
                record.EstimatedCost);
        }
    }
}
