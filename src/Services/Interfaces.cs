using System.Security.Claims;
using Azure.Search.Documents;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

public interface IRetrievalService
{
    Task<RetrievalResult> RetrieveAsync(AskRequestDto request, ClaimsPrincipal user, CancellationToken cancellationToken);
    string BuildSecurityFilter(UserClaimsContext userClaims);
    UserClaimsContext GetUserClaims(ClaimsPrincipal user);
}

public interface IChatService
{
    Task<ChatGenerationResult> GenerateStructuredAnswerAsync(ChatRequest request, CancellationToken cancellationToken);
    string ModelUsed { get; }
}

public interface IPromptSecurityService
{
    PromptSecurityResult AnalyzePrompt(string input);
}

public interface IQueryRewriteService
{
    Task<string> RewriteAsync(string question, CancellationToken cancellationToken);
}

public interface IConfidenceService
{
    string CalculateConfidence(IReadOnlyList<RetrievedChunk> chunks, double averageScore, IReadOnlyList<CitationDto> citations);
}

public interface IMemoryService
{
    Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(string conversationId, int maxMessages, CancellationToken cancellationToken);
    Task AppendMessagesAsync(string conversationId, IEnumerable<ConversationMessage> messages, int maxMessages, CancellationToken cancellationToken);
}

public interface IAuditService
{
    Task RecordAskAuditAsync(AskAuditRecord record, CancellationToken cancellationToken);
}

public interface IMetricsService
{
    void TrackQueryAudit(AskAuditRecord record);
    MetricsSnapshotDto GetSnapshot(DateTimeOffset nowUtc);
}

public interface IProvenanceService
{
    List<CitationDto> EnrichCitations(IReadOnlyList<CitationDto> citations, IReadOnlyList<RetrievedChunk> chunks);
}

public interface IIndexVersionService
{
    string GetActiveIndex();
    IndexVersionState GetState();
    void SwitchActiveIndex(string indexVersion);
    SearchClient CreateSearchClientForActiveIndex();
    SearchClient CreateSearchClient(string indexName);
}

public interface ILineageService
{
    Task<IReadOnlyList<DocumentLineageRecord>> GetLineageAsync(CancellationToken cancellationToken);
    Task AppendAsync(DocumentLineageRecord record, CancellationToken cancellationToken);
}

public interface IReindexService
{
    Task<int> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken);
}

public interface IHealthCheckService
{
    Task<HealthStatusDto> GetHealthAsync(CancellationToken cancellationToken);
}
