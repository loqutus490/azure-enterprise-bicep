using System.Diagnostics;
using System.Security.Claims;
using LegalRagApp.Middleware;
using LegalRagApp.Models;
using LegalRagApp.Prompts;
using LegalRagApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalRagApp.Controllers;

[ApiController]
[Route("")]
public sealed class AskController : ControllerBase
{
    private readonly IRetrievalService _retrievalService;
    private readonly IChatService _chatService;
    private readonly IQueryRewriteService _queryRewriteService;
    private readonly IConfidenceService _confidenceService;
    private readonly IProvenanceService _provenanceService;
    private readonly IMemoryService _memoryService;
    private readonly ILogger<AskController> _logger;
    private readonly IWebHostEnvironment _environment;
    private readonly IAuthorizationService _authorizationService;
    private readonly bool _bypassAuthInDevelopment;
    private readonly bool _bypassMatterAuthorizationInDevelopment;
    private readonly int _conversationMemoryDepth;
    private readonly bool _enableAzureAd;
    private readonly bool _debugRagEnabled;

    public AskController(
        IRetrievalService retrievalService,
        IChatService chatService,
        IQueryRewriteService queryRewriteService,
        IConfidenceService confidenceService,
        IProvenanceService provenanceService,
        IMemoryService memoryService,
        IAuthorizationService authorizationService,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<AskController> logger)
    {
        _retrievalService = retrievalService;
        _chatService = chatService;
        _queryRewriteService = queryRewriteService;
        _confidenceService = confidenceService;
        _provenanceService = provenanceService;
        _memoryService = memoryService;
        _authorizationService = authorizationService;
        _enableAzureAd = configuration.GetValue<bool?>("Authorization:EnableAzureAd") ?? true;
        _environment = environment;
        _logger = logger;
        _conversationMemoryDepth = configuration.GetValue<int?>("Rag:ConversationMemoryDepth") ?? 5;
        _bypassAuthInDevelopment = environment.IsDevelopment() && configuration.GetValue<bool>("Authorization:BypassAuthInDevelopment");
        _bypassMatterAuthorizationInDevelopment = environment.IsDevelopment() && configuration.GetValue<bool>("Authorization:BypassMatterAuthorizationInDevelopment");
        _debugRagEnabled = configuration.GetValue<bool?>("DebugRag:Enabled") ?? string.Equals(configuration["DEBUG_RAG"], "true", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequestDto request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!await ValidateAccessAsync())
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Question)) return BadRequest("Question is required.");
        if (string.IsNullOrWhiteSpace(request.MatterId)) return BadRequest("matterId is required.");
        if (string.IsNullOrWhiteSpace(request.ConversationId)) return BadRequest("conversationId is required.");

        var finalStatus = "error";
        try
        {
            var sanitizedQuestion = HttpContext.Items.TryGetValue("SanitizedQuestion", out var sanitized)
                ? sanitized?.ToString() ?? request.Question
                : request.Question;
            var rewrittenQuery = await _queryRewriteService.RewriteAsync(sanitizedQuestion, cancellationToken);

            var retrievalRequest = new AskRequestDto
            {
                Question = rewrittenQuery,
                MatterId = request.MatterId,
                ConversationId = request.ConversationId,
                PracticeArea = request.PracticeArea,
                Client = request.Client,
                ConfidentialityLevel = request.ConfidentialityLevel
            };

            var retrievalUser = _bypassMatterAuthorizationInDevelopment
                ? BuildBypassUser(HttpContext.User, request.MatterId)
                : HttpContext.User;
            var retrieval = await _retrievalService.RetrieveAsync(retrievalRequest, retrievalUser, cancellationToken);

            var conversationHistory = await _memoryService.GetRecentMessagesAsync(request.ConversationId, _conversationMemoryDepth, cancellationToken);

            StructuredAnswerDto structured;
            int promptTokens = 0;
            int completionTokens = 0;
            double estimatedCost = 0;
            var fallbackReason = retrieval.FallbackReason;

            if (retrieval.Chunks.Count == 0)
            {
                structured = PromptOutputFactory.BuildInsufficientContextFallback();
                finalStatus = fallbackReason ?? "fallback_empty_context";
            }
            else
            {
                var generated = await _chatService.GenerateStructuredAnswerAsync(new ChatRequest
                {
                    Question = request.Question,
                    RetrievedChunks = retrieval.Chunks,
                    ConversationHistory = conversationHistory
                }, cancellationToken);
                structured = generated.Answer;
                promptTokens = generated.PromptTokens;
                completionTokens = generated.CompletionTokens;
                estimatedCost = generated.EstimatedCost;
                finalStatus = structured.Summary.Contains(PromptOutputFactory.InsufficientContextSummary, StringComparison.OrdinalIgnoreCase)
                    ? "fallback_empty_context"
                    : "grounded_success";
            }

            structured.Citations = _provenanceService.EnrichCitations(structured.Citations, retrieval.Chunks);
            structured.Confidence = _confidenceService.CalculateConfidence(retrieval.Chunks, retrieval.AverageScore, structured.Citations);

            await _memoryService.AppendMessagesAsync(request.ConversationId, new[]
            {
                new ConversationMessage { Role = "user", Content = request.Question, TimestampUtc = DateTimeOffset.UtcNow },
                new ConversationMessage { Role = "assistant", Content = structured.Summary, TimestampUtc = DateTimeOffset.UtcNow }
            }, _conversationMemoryDepth, cancellationToken);

            var sourceMetadata = retrieval.Chunks.Select(c => new AskSourceDto
            {
                SourceFile = c.SourceFile ?? "unknown",
                SourceId = c.SourceId ?? c.DocumentId ?? "unknown",
                MatterId = c.MatterId ?? "unknown",
                DocumentType = c.DocumentType ?? "unknown"
            }).DistinctBy(s => $"{s.SourceId}|{s.SourceFile}", StringComparer.OrdinalIgnoreCase).ToList();

            var response = new AskResponseDto
            {
                Answer = structured.Summary,
                KeyPoints = structured.KeyPoints,
                Citations = structured.Citations,
                ConversationId = request.ConversationId,
                Confidence = structured.Confidence,
                RewrittenQuery = rewrittenQuery,
                Sources = sourceMetadata.Select(s => s.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                RetrievedChunkCount = retrieval.FilteredRetrievedChunkCount,
                SourceMetadata = sourceMetadata,
                Diagnostics = _debugRagEnabled
                    ? new AskDiagnosticsSummaryDto
                    {
                        RawRetrievalCount = retrieval.RawRetrievedChunkCount,
                        FilteredRetrievalCount = retrieval.FilteredRetrievedChunkCount,
                        FinalAnswerStatus = finalStatus,
                        FallbackReason = fallbackReason
                    }
                    : null
            };

            var correlationId = HttpContext.TraceIdentifier;
            var claimsSummary = BuildClaimsSummary(retrieval.UserClaims);
            HttpContext.Items[AuditLoggingMiddleware.AuditRecordItemKey] = new AskAuditRecord
            {
                CorrelationId = correlationId,
                UserId = retrieval.UserClaims.UserId,
                ClaimsSummary = claimsSummary,
                Question = request.Question,
                RewrittenQuery = rewrittenQuery,
                Timestamp = DateTimeOffset.UtcNow,
                DocumentsRetrieved = response.Sources,
                ModelUsed = _chatService.ModelUsed,
                Confidence = response.Confidence,
                ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                EstimatedCost = estimatedCost,
                RetrievalCount = retrieval.RawRetrievedChunkCount,
                FilteredCount = retrieval.FilteredRetrievedChunkCount,
                FinalAnswerStatus = finalStatus
            };

            _logger.LogInformation("Ask handled {@AuditEvent}", new
            {
                timestamp = DateTimeOffset.UtcNow,
                correlationId,
                user = retrieval.UserClaims.UserId,
                claimsSummary,
                queryText = request.Question,
                retrievalCount = retrieval.RawRetrievedChunkCount,
                filteredCount = retrieval.FilteredRetrievedChunkCount,
                sources = sourceMetadata,
                finalAnswerStatus = finalStatus
            });

            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "AskRequest denied by matter RBAC. MatterId={MatterId}", request.MatterId);
            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AskRequest failed. QuestionLength={QuestionLength} DurationMs={DurationMs}", request.Question.Length, stopwatch.ElapsedMilliseconds);
            if (_environment.IsDevelopment())
                return Problem($"Unable to process request at this time. {ex.GetType().Name}: {ex.Message}");

            return Problem("Unable to process request at this time.");
        }
    }

    [HttpPost("debug/retrieval")]
    public async Task<IActionResult> DebugRetrieval([FromBody] AskRequestDto request, CancellationToken cancellationToken)
    {
        if (!_debugRagEnabled)
            return NotFound();

        if (!await ValidateAccessAsync())
            return Unauthorized();

        var debug = await _retrievalService.BuildDebugAsync(request, HttpContext.User, cancellationToken);
        return Ok(debug);
    }

    private async Task<bool> ValidateAccessAsync()
    {
        if (_bypassAuthInDevelopment || !_enableAzureAd)
            return true;

        if (User?.Identity?.IsAuthenticated != true)
            return false;

        var authResult = await _authorizationService.AuthorizeAsync(User, policyName: "ApiAccessPolicy");
        return authResult.Succeeded;
    }

    private static ClaimsPrincipal BuildBypassUser(ClaimsPrincipal original, string matterId)
    {
        var identity = new ClaimsIdentity(original.Claims, "Bypass");
        identity.AddClaim(new Claim("permittedMatters", matterId));
        return new ClaimsPrincipal(identity);
    }

    private static string BuildClaimsSummary(UserClaimsContext userClaims) =>
        $"matters={string.Join(',', userClaims.PermittedMatters.OrderBy(m => m))};groups={string.Join(',', userClaims.Groups.OrderBy(g => g))}";
}
