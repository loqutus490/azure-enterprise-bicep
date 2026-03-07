using System.Diagnostics;
using LegalRagApp.Middleware;
using LegalRagApp.Models;
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
        // Assume Azure AD enforcement unless explicitly toggled off to avoid bypassing auth unexpectedly.
        _enableAzureAd = configuration.GetValue<bool?>("Authorization:EnableAzureAd") ?? true;
        _environment = environment;
        _logger = logger;
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

            RetrievalResult retrieval;
            if (_bypassMatterAuthorizationInDevelopment)
            {
                // Keep existing local-debug behavior while still applying request filters.
                retrieval = await _retrievalService.RetrieveAsync(
                    retrievalRequest,
                    BuildBypassUser(HttpContext.User, request.MatterId),
                    cancellationToken);
            }
            else
            {
                retrieval = await _retrievalService.RetrieveAsync(retrievalRequest, HttpContext.User, cancellationToken);
            }

            var conversationHistory = await _memoryService.GetRecentMessagesAsync(
                request.ConversationId,
                _conversationMemoryDepth,
                cancellationToken);

            StructuredAnswerDto structured;
            int promptTokens = 0;
            int completionTokens = 0;
            double estimatedCost = 0.0;
            if (retrieval.Chunks.Count == 0)
            {
                structured = new StructuredAnswerDto
                {
                    Summary = "I cannot find this information in the provided materials.",
                    KeyPoints = new List<string>(),
                    Citations = new List<CitationDto>(),
                    Confidence = "low"
                };
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
            }

            structured.Citations = _provenanceService.EnrichCitations(structured.Citations, retrieval.Chunks);

            var confidence = _confidenceService.CalculateConfidence(
                retrieval.Chunks,
                retrieval.AverageScore,
                structured.Citations);
            structured.Confidence = confidence;

            await _memoryService.AppendMessagesAsync(request.ConversationId, new[]
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
            }, _conversationMemoryDepth, cancellationToken);

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
                RetrievedChunkCount = retrieval.RetrievedChunkCount
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
                "AskRequest completed. ConversationId={ConversationId} MatterId={MatterId} Filter={Filter} RetrievedChunkCount={RetrievedChunkCount} DurationMs={DurationMs}",
                request.ConversationId,
                request.MatterId,
                retrieval.SearchFilter,
                retrieval.RetrievedChunkCount,
                stopwatch.ElapsedMilliseconds);

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

    private static System.Security.Claims.ClaimsPrincipal BuildBypassUser(System.Security.Claims.ClaimsPrincipal original, string matterId)
    {
        var identity = new System.Security.Claims.ClaimsIdentity(original.Claims, "Bypass");
        identity.AddClaim(new System.Security.Claims.Claim("permittedMatters", matterId));
        return new System.Security.Claims.ClaimsPrincipal(identity);
    }
}
