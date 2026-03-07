using System.Text.Json;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

public sealed class AuditService : IAuditService
{
    private readonly ILogger<AuditService> _logger;
    private readonly IMetricsService _metricsService;

    public AuditService(ILogger<AuditService> logger, IMetricsService metricsService)
    {
        _logger = logger;
        _metricsService = metricsService;
    }

    public Task RecordAskAuditAsync(AskAuditRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new
        {
            userId = record.UserId,
            question = record.Question,
            rewrittenQuery = record.RewrittenQuery,
            timestamp = record.Timestamp,
            documentsRetrieved = record.DocumentsRetrieved,
            modelUsed = record.ModelUsed,
            confidence = record.Confidence,
            promptTokens = record.PromptTokens,
            completionTokens = record.CompletionTokens,
            estimatedCost = record.EstimatedCost,
            responseTimeMs = record.ResponseTimeMs
        };

        _metricsService.TrackQueryAudit(record);
        _logger.LogInformation("AUDIT_AI_QUERY {AuditPayload}", JsonSerializer.Serialize(payload));
        return Task.CompletedTask;
    }
}
