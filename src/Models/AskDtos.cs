namespace LegalRagApp.Models;

public sealed class AskRequestDto
{
    public string Question { get; set; } = string.Empty;
    public string MatterId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string? PracticeArea { get; set; }
    public string? Client { get; set; }
    public string? ConfidentialityLevel { get; set; }
}

public sealed class AskResponseDto
{
    public string Answer { get; set; } = string.Empty;
    public List<string> KeyPoints { get; set; } = new();
    public List<CitationDto> Citations { get; set; } = new();
    public string ConversationId { get; set; } = string.Empty;
    public string Confidence { get; set; } = "medium";
    public string RewrittenQuery { get; set; } = string.Empty;

    // Keep legacy fields for backward compatibility.
    public string[] Sources { get; set; } = Array.Empty<string>();
    public int RetrievedChunkCount { get; set; }

    // New enterprise response metadata.
    public List<AskSourceDto> SourceMetadata { get; set; } = new();
    public AskDiagnosticsSummaryDto? Diagnostics { get; set; }
}

public sealed class AskSourceDto
{
    public string SourceFile { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string MatterId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string AccessGroup { get; set; } = string.Empty;
}

public sealed class AskDiagnosticsSummaryDto
{
    public int RawRetrievalCount { get; set; }
    public int FilteredRetrievalCount { get; set; }
    public string FinalAnswerStatus { get; set; } = string.Empty;
    public string? FallbackReason { get; set; }
}

public sealed class CitationDto
{
    public string DocumentId { get; set; } = string.Empty;
    public string Document { get; set; } = string.Empty;
    public int? Page { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public string? Version { get; set; }
    public DateTimeOffset? IngestedAt { get; set; }
    public string? Checksum { get; set; }
}

public sealed class StructuredAnswerDto
{
    public string Summary { get; set; } = string.Empty;
    public List<string> KeyPoints { get; set; } = new();
    public List<CitationDto> Citations { get; set; } = new();
    public string Confidence { get; set; } = "medium";
}

public sealed class RetrievedChunk
{
    public string? DocumentId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string? SourceFile { get; set; }
    public string? SourceId { get; set; }
    public int? Page { get; set; }
    public double? Score { get; set; }
    public string? DocumentVersion { get; set; }
    public DateTimeOffset? IngestionTimestamp { get; set; }
    public string? Checksum { get; set; }
    public string? MatterId { get; set; }
    public string? PracticeArea { get; set; }
    public string? Client { get; set; }
    public string? ConfidentialityLevel { get; set; }
    public string? AccessGroup { get; set; }
    public string? DocumentType { get; set; }
}

public sealed class RetrievalResult
{
    public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = Array.Empty<RetrievedChunk>();
    public string SearchFilter { get; init; } = string.Empty;
    public int RawRetrievedChunkCount { get; init; }
    public int FilteredRetrievedChunkCount { get; init; }
    public double AverageScore { get; init; }
    public UserClaimsContext UserClaims { get; init; } = new();
    public string? FallbackReason { get; init; }
}

public sealed class PromptContext
{
    public string SystemPrompt { get; init; } = string.Empty;
    public string UserPrompt { get; init; } = string.Empty;
    public string ContextPreview { get; init; } = string.Empty;
}

public sealed class RetrievalDebugResponseDto
{
    public string Query { get; set; } = string.Empty;
    public UserClaimsContext User { get; set; } = new();
    public int RawRetrievalCount { get; set; }
    public int FilteredRetrievalCount { get; set; }
    public List<AskSourceDto> Sources { get; set; } = new();
    public string PromptContextPreview { get; set; } = string.Empty;
    public string? FallbackReason { get; set; }
}

public sealed class ConversationMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConversationMemory
{
    public string ConversationId { get; set; } = string.Empty;
    public List<ConversationMessage> Messages { get; set; } = new();
}

public sealed class UserClaimsContext
{
    public string UserId { get; init; } = "anonymous";
    public string Email { get; init; } = string.Empty;
    public string Role { get; init; } = "unknown";
    public HashSet<string> PermittedMatters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();
}

public sealed class ChatRequest
{
    public string Question { get; init; } = string.Empty;
    public IReadOnlyList<RetrievedChunk> RetrievedChunks { get; init; } = Array.Empty<RetrievedChunk>();
    public IReadOnlyList<ConversationMessage> ConversationHistory { get; init; } = Array.Empty<ConversationMessage>();
}

public sealed class AskAuditRecord
{
    public string CorrelationId { get; init; } = string.Empty;
    public string UserId { get; init; } = "anonymous";
    public string ClaimsSummary { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string RewrittenQuery { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<string> DocumentsRetrieved { get; init; } = Array.Empty<string>();
    public string ModelUsed { get; init; } = string.Empty;
    public string Confidence { get; init; } = "low";
    public long ResponseTimeMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double EstimatedCost { get; init; }
    public int RetrievalCount { get; init; }
    public int FilteredCount { get; init; }
    public string FinalAnswerStatus { get; init; } = "error";
}

public sealed class PromptSecurityResult
{
    public bool IsAllowed { get; init; }
    public string? Reason { get; init; }
    public string SanitizedPrompt { get; init; } = string.Empty;
}

public sealed class MetricsSnapshotDto
{
    public int QueriesToday { get; init; }
    public long AvgResponseTimeMs { get; init; }
    public string AvgConfidence { get; init; } = "low";
    public List<string> MostCommonQueries { get; init; } = new();
    public List<string> DocumentsAccessed { get; init; } = new();
}

public sealed class ChatGenerationResult
{
    public StructuredAnswerDto Answer { get; init; } = new();
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double EstimatedCost { get; init; }
}

public sealed class IndexVersionState
{
    public string ActiveIndex { get; set; } = string.Empty;
    public List<string> KnownIndexes { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SwitchIndexRequest
{
    public string IndexVersion { get; set; } = string.Empty;
}

public sealed class DocumentLineageRecord
{
    public string DocumentId { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public DateTimeOffset IngestionTimestamp { get; set; } = DateTimeOffset.UtcNow;
    public int IndexedChunks { get; set; }
    public string IndexVersion { get; set; } = string.Empty;
    public string EventType { get; set; } = "ingest";
}

public sealed class HealthStatusDto
{
    public string Status { get; init; } = "healthy";
    public string VectorIndex { get; init; } = string.Empty;
    public string OpenAI { get; init; } = "unknown";
    public string SearchService { get; init; } = "unknown";
    public string Redis { get; init; } = "unknown";
}
