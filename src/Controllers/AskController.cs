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
        _environment = environment;
        _logger = logger;

        _enableAzureAd = configuration.GetValue<bool?>("Authorization:EnableAzureAd") ?? true;
        _conversationMemoryDepth = configuration.GetValue<int?>("Rag:ConversationMemoryDepth") ?? 5;
        _bypassAuthInDevelopment = environment.IsDevelopment()
            && configuration.GetValue<bool>("Authorization:BypassAuthInDevelopment");
        _bypassMatterAuthorizationInDevelopment = environment.IsDevelopment()
            && configuration.GetValue<bool>("Authorization:BypassMatterAuthorizationInDevelopment");
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequestDto request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestTraceId = HttpContext.TraceIdentifier;

        if (!_bypassAuthInDevelopment && _enableAzureAd)
        {
            if (User?.Identity?.IsAuthenticated != true)
                return Unauthorized();

            var authResult = await _authorizationService.AuthorizeAsync(User, policyName: "ApiAccessPolicy");
            if (!authResult.Succeeded)
                return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest("Question is required.");

        if (string.IsNullOrWhiteSpace(request.MatterId))
            return BadRequest("matterId is required.");

        if (string.IsNullOrWhiteSpace(request.ConversationId))
            return BadRequest("conversationId is required.");

        try
        {
            _logger.LogInformation(
                "AskRequest started. TraceId={TraceId} ConversationId={ConversationId} MatterId={MatterId} QuestionLength={QuestionLength}",
                requestTraceId,
                request.ConversationId,
                request.MatterId,
                request.Question.Length);

            var sanitizedQuestion = HttpContext.Items.TryGetValue("SanitizedQuestion", out var sanitized)
                ? sanitized?.ToString() ?? request.Question
                : request.Question;

            var rewriteStopwatch = Stopwatch.StartNew();
            var rewrittenQuery = await _queryRewriteService.RewriteAsync(sanitizedQuestion, cancellationToken);
            rewriteStopwatch.Stop();

            _logger.LogInformation(
                "AskRequest rewrite completed. TraceId={TraceId} ConversationId={ConversationId} RewriteDurationMs={RewriteDurationMs}",
                requestTraceId,
                request.ConversationId,
                rewriteStopwatch.ElapsedMilliseconds);

            var retrievalRequest = new AskRequestDto
            {
                Question = rewrittenQuery,
                MatterId = request.MatterId,
                ConversationId = request.ConversationId,
                PracticeArea = request.PracticeArea,
                Client = request.Client,
                ConfidentialityLevel = request.ConfidentialityLevel
            };

            var retrievalStopwatch = Stopwatch.StartNew();

            RetrievalResult retrieval;
            if (_bypassMatterAuthorizationInDevelopment)
            {
                retrieval = await _retrievalService.RetrieveAsync(
                    retrievalRequest,
                    BuildBypassUser(HttpContext.User, request.MatterId),
                    cancellationToken);
            }
            else
            {
                retrieval = await _retrievalService.RetrieveAsync(
                    retrievalRequest,
                    HttpContext.User,
                    cancellationToken);
            }

            retrievalStopwatch.Stop();

            _logger.LogInformation(
                "AskRequest retrieval completed. TraceId={TraceId} ConversationId={ConversationId} MatterId={MatterId} RetrievedChunkCount={RetrievedChunkCount} AverageScore={AverageScore} RetrievalDurationMs={RetrievalDurationMs}",
                requestTraceId,
                request.ConversationId,
                request.MatterId,
                retrieval.FilteredRetrievedChunkCount,
                retrieval.AverageScore,
                retrievalStopwatch.ElapsedMilliseconds);

            var memoryStopwatch = Stopwatch.StartNew();

            var conversationHistory = await _memoryService.GetRecentMessagesAsync(
                request.ConversationId,
                _conversationMemoryDepth,
                cancellationToken);

            memoryStopwatch.Stop();

            _logger.LogInformation(
                "AskRequest memory loaded. TraceId={TraceId} ConversationId={ConversationId} MessageCount={MessageCount} MemoryDurationMs={MemoryDurationMs}",
                requestTraceId,
                request.ConversationId,
                conversationHistory.Count,
                memoryStopwatch.ElapsedMilliseconds);

            StructuredAnswerDto structured;
            int promptTokens = 0;
            int completionTokens = 0;
            double estimatedCost = 0.0;

            var generationStopwatch = Stopwatch.StartNew();

            if (retrieval.Chunks.Count == 0)
            {
                structured = PromptOutputFactory.BuildInsufficientContextFallback();
            }
            else
            {
                var generated = await _chatService.GenerateStructuredAnswerAsync(
                    new ChatRequest
                    {
                        Question = request.Question,
                        RetrievedChunks = retrieval.Chunks,
                        ConversationHistory = conversationHistory
                    },
                    cancellationToken);

                structured = generated.Answer;
                promptTokens = generated.PromptTokens;
                completionTokens = generated.CompletionTokens;
                estimatedCost = generated.EstimatedCost;
            }

            generationStopwatch.Stop();

            _logger.LogInformation(
                "AskRequest generation completed. TraceId={TraceId} ConversationId={ConversationId} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} EstimatedCost={EstimatedCost} GenerationDurationMs={GenerationDurationMs}",
                requestTraceId,
                request.ConversationId,
                promptTokens,
                completionTokens,
                estimatedCost,
                generationStopwatch.ElapsedMilliseconds);

            structured.Citations = _provenanceService.EnrichCitations(structured.Citations, retrieval.Chunks);

            var confidence = _confidenceService.CalculateConfidence(
                retrieval.Chunks,
                retrieval.AverageScore,
                structured.Citations);

            structured.Confidence = confidence;

            await _memoryService.AppendMessagesAsync(
                request.ConversationId,
                new[]
                {
                    new ConversationMessage
                    {
                        Role = "user",
                        Content = request.Question,
                        TimestampUtc = DateTimeOffset.UtcNow
                    },
                    new ConversationMessage
                    {
                        Role = "assistant",
                        Content = structured.Summary,
                        TimestampUtc = DateTimeOffset.UtcNow
                    }
                },
                _conversationMemoryDepth,
                cancellationToken);

            var finalAnswerStatus = retrieval.Chunks.Count > 0
                ? "grounded_success"
                : (retrieval.FallbackReason ?? "fallback_unknown");

            var response = new AskResponseDto
            {
                Answer = structured.Summary,
                KeyPoints = structured.KeyPoints,
                Citations = structured.Citations,
                ConversationId = request.ConversationId,
                Confidence = structured.Confidence,
                RewrittenQuery = rewrittenQuery,
                Sources = retrieval.Chunks
                    .Select(c => c.SourceFile)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToArray(),
                RetrievedChunkCount = retrieval.FilteredRetrievedChunkCount,
                SourceMetadata = retrieval.Chunks.Select(c => new AskSourceDto
                {
                    SourceFile = c.SourceFile ?? string.Empty,
                    SourceId = c.SourceId ?? string.Empty,
                    MatterId = c.MatterId ?? string.Empty,
                    DocumentType = c.DocumentType ?? string.Empty
                }).ToList(),
                Diagnostics = new AskDiagnosticsSummaryDto
                {
                    RawRetrievalCount = retrieval.RawRetrievedChunkCount,
                    FilteredRetrievalCount = retrieval.FilteredRetrievedChunkCount,
                    FinalAnswerStatus = finalAnswerStatus,
                    FallbackReason = retrieval.FallbackReason
                }
            };

            HttpContext.Items[AuditLoggingMiddleware.AuditRecordItemKey] = new AskAuditRecord
            {
                UserId = retrieval.UserClaims.UserId,
                Question = request.Question,
                RewrittenQuery = rewrittenQuery,
                Timestamp = DateTimeOffset.UtcNow,
                DocumentsRetrieved = response.Sources,
                ModelUsed = _chatService.ModelUsed,
                Confidence = response.Confidence,
                ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                EstimatedCost = estimatedCost
            };

            _logger.LogInformation(
                "AskRequest completed. TraceId={TraceId} ConversationId={ConversationId} MatterId={MatterId} Filter={Filter} RetrievedChunkCount={RetrievedChunkCount} Confidence={Confidence} DurationMs={DurationMs}",
                requestTraceId,
                request.ConversationId,
                request.MatterId,
                retrieval.SearchFilter,
                retrieval.FilteredRetrievedChunkCount,
                response.Confidence,
                stopwatch.ElapsedMilliseconds);

            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "AskRequest denied by matter RBAC. TraceId={TraceId} ConversationId={ConversationId} MatterId={MatterId}",
                requestTraceId,
                request.ConversationId,
                request.MatterId);

            return Unauthorized();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AskRequest failed. TraceId={TraceId} ConversationId={ConversationId} MatterId={MatterId} QuestionLength={QuestionLength} DurationMs={DurationMs}",
                requestTraceId,
                request.ConversationId,
                request.MatterId,
                request.Question.Length,
                stopwatch.ElapsedMilliseconds);

            if (_environment.IsDevelopment())
                return Problem($"Unable to process request at this time. {ex.GetType().Name}: {ex.Message}");

            return Problem("Unable to process request at this time.");
        }
    }

    private static ClaimsPrincipal BuildBypassUser(ClaimsPrincipal originalUser, string matterId)
    {
        var claims = originalUser.Claims.ToList();

        if (!string.IsNullOrWhiteSpace(matterId))
        {
            if (!claims.Any(c => string.Equals(c.Type, "matter_id", StringComparison.OrdinalIgnoreCase) && c.Value == matterId))
                claims.Add(new Claim("matter_id", matterId));

            if (!claims.Any(c => string.Equals(c.Type, "matterId", StringComparison.OrdinalIgnoreCase) && c.Value == matterId))
                claims.Add(new Claim("matterId", matterId));

            if (!claims.Any(c => string.Equals(c.Type, "MatterId", StringComparison.OrdinalIgnoreCase) && c.Value == matterId))
                claims.Add(new Claim("MatterId", matterId));
        }

        if (!claims.Any(c => c.Type == ClaimTypes.NameIdentifier))
            claims.Add(new Claim(ClaimTypes.NameIdentifier, "local-dev-user"));

        if (!claims.Any(c => c.Type == ClaimTypes.Name))
            claims.Add(new Claim(ClaimTypes.Name, "Local Development User"));

        var identity = new ClaimsIdentity(claims, "DevelopmentBypass");
        return new ClaimsPrincipal(identity);
    }
}
