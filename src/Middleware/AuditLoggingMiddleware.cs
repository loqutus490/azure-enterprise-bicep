using LegalRagApp.Models;
using LegalRagApp.Services;

namespace LegalRagApp.Middleware;

public sealed class AuditLoggingMiddleware
{
    public const string AuditRecordItemKey = "AskAuditRecord";

    private readonly RequestDelegate _next;

    public AuditLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAuditService auditService)
    {
        try
        {
            await _next(context);
        }
        finally
        {
            if (context.Request.Path.Equals("/ask", StringComparison.OrdinalIgnoreCase)
                && context.Items.TryGetValue(AuditRecordItemKey, out var value)
                && value is AskAuditRecord record)
            {
                await auditService.RecordAskAuditAsync(record, context.RequestAborted);
            }
        }
    }
}
