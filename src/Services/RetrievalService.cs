using System.Security.Claims;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

public sealed class RetrievalService : IRetrievalService
{
    private readonly IIndexVersionService _indexVersionService;
    private readonly IAuthorizationFilter _authorizationFilter;
    private readonly IPromptBuilder _promptBuilder;
    private readonly OpenAI.Embeddings.EmbeddingClient _embeddingClient;
    private readonly ILogger<RetrievalService> _logger;
    private readonly int _embeddingNeighbors;
    private readonly int _topChunks;
    private readonly int _maxChunkChars;
    private readonly int? _embeddingDimensions;
    private readonly List<string> _baseSelectFields =
    [
        "id", "content", "source", "sourceId", "page", "matterId", "practiceArea", "client",
        "confidentialityLevel", "documentVersion", "ingestionTimestamp", "checksum", "accessGroup", "documentType"
    ];
    private readonly HashSet<string> _missingSelectFields = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _aclFilterEnabled;

    public RetrievalService(
        IIndexVersionService indexVersionService,
        IAuthorizationFilter authorizationFilter,
        IPromptBuilder promptBuilder,
        AzureOpenAIClient openAiClient,
        IConfiguration configuration,
        ILogger<RetrievalService> logger)
    {
        _indexVersionService = indexVersionService;
        _authorizationFilter = authorizationFilter;
        _promptBuilder = promptBuilder;
        _logger = logger;

        var embeddingDeployment = configuration["AzureOpenAI:EmbeddingDeployment"];
        if (string.IsNullOrWhiteSpace(embeddingDeployment))
            throw new InvalidOperationException("Missing configuration: AzureOpenAI:EmbeddingDeployment");

        _embeddingClient = openAiClient.GetEmbeddingClient(embeddingDeployment);
        _embeddingDimensions = configuration.GetValue<int?>("AzureOpenAI:EmbeddingDimensions");
        _embeddingNeighbors = configuration.GetValue<int?>("Rag:KNearestNeighbors") ?? 5;
        _topChunks = configuration.GetValue<int?>("Rag:TopChunks") ?? 5;
        _maxChunkChars = configuration.GetValue<int?>("Rag:MaxContextCharacters") ?? 12000;
        _aclFilterEnabled = configuration.GetValue<bool?>("Authorization:EnableAclFilter") ?? true;
    }

    public async Task<RetrievalResult> RetrieveAsync(AskRequestDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userClaims = _authorizationFilter.GetUserClaims(user);
        if (!_authorizationFilter.IsMatterAuthorized(userClaims, request.MatterId))
            throw new UnauthorizedAccessException($"Matter access denied for {request.MatterId}.");

        var rawChunks = await RetrieveRawChunksAsync(request, userClaims, cancellationToken);
        var filteredChunks = _authorizationFilter.FilterAuthorizedChunks(rawChunks, userClaims);

        var averageScore = filteredChunks.Count == 0
            ? 0.0
            : filteredChunks.Where(c => c.Score.HasValue).Select(c => c.Score!.Value).DefaultIfEmpty(0.0).Average();

        var fallbackReason = filteredChunks.Count == 0
            ? rawChunks.Count == 0 ? "fallback_no_docs" : "fallback_unauthorized"
            : null;

        return new RetrievalResult
        {
            Chunks = filteredChunks,
            SearchFilter = BuildSearchFilter(request, userClaims),
            RawRetrievedChunkCount = rawChunks.Count,
            FilteredRetrievedChunkCount = filteredChunks.Count,
            AverageScore = averageScore,
            UserClaims = userClaims,
            FallbackReason = fallbackReason
        };
    }

    public async Task<RetrievalDebugResponseDto> BuildDebugAsync(AskRequestDto request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var retrieval = await RetrieveAsync(request, user, cancellationToken);
        var prompt = _promptBuilder.BuildPrompt(request.Question, retrieval.Chunks, Array.Empty<ConversationMessage>());

        return new RetrievalDebugResponseDto
        {
            Query = request.Question,
            User = retrieval.UserClaims,
            RawRetrievalCount = retrieval.RawRetrievedChunkCount,
            FilteredRetrievalCount = retrieval.FilteredRetrievedChunkCount,
            PromptContextPreview = prompt.ContextPreview,
            FallbackReason = retrieval.FallbackReason,
            Sources = retrieval.Chunks.Select(ToSourceMetadata).ToList()
        };
    }

    private async Task<List<RetrievedChunk>> RetrieveRawChunksAsync(AskRequestDto request, UserClaimsContext userClaims, CancellationToken cancellationToken)
    {
        var baseFilters = new List<string>
        {
            _authorizationFilter.BuildSecurityFilter(userClaims),
            $"matterId eq '{EscapeODataString(request.MatterId)}'"
        };

        if (!string.IsNullOrWhiteSpace(request.PracticeArea))
            baseFilters.Add($"practiceArea eq '{EscapeODataString(request.PracticeArea)}'");
        if (!string.IsNullOrWhiteSpace(request.Client))
            baseFilters.Add($"client eq '{EscapeODataString(request.Client)}'");
        if (!string.IsNullOrWhiteSpace(request.ConfidentialityLevel))
            baseFilters.Add($"confidentialityLevel eq '{EscapeODataString(request.ConfidentialityLevel)}'");

        var aclFilter = _authorizationFilter.BuildAclFilter(userClaims);

        var embeddingOptions = new OpenAI.Embeddings.EmbeddingGenerationOptions();
        if (_embeddingDimensions.HasValue)
            embeddingOptions.Dimensions = _embeddingDimensions.Value;

        var embeddingResponse = await _embeddingClient.GenerateEmbeddingAsync(request.Question, options: embeddingOptions, cancellationToken: cancellationToken);
        var questionVector = embeddingResponse.Value.ToFloats().ToArray();

        var searchOptions = new SearchOptions
        {
            Size = _topChunks,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(questionVector)
                    {
                        KNearestNeighborsCount = _embeddingNeighbors,
                        Fields = { "contentVector" }
                    }
                }
            }
        };

        foreach (var field in GetSelectFields())
            searchOptions.Select.Add(field);

        var searchClient = _indexVersionService.CreateSearchClientForActiveIndex();
        var searchResponse = await ExecuteSearchAsync(searchClient, request.Question, searchOptions, baseFilters, aclFilter, cancellationToken);

        var chunks = new List<RetrievedChunk>();
        await foreach (var result in searchResponse.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var content = result.Document.TryGetValue("content", out var contentObj) ? contentObj?.ToString() ?? string.Empty : string.Empty;
            chunks.Add(new RetrievedChunk
            {
                DocumentId = result.Document.TryGetValue("id", out var idObj) ? idObj?.ToString() : null,
                Content = content,
                Snippet = BuildSnippet(content, Math.Min(_maxChunkChars / Math.Max(_topChunks, 1), 1200)),
                SourceFile = GetPreferredSourceFile(result.Document),
                SourceId = result.Document.TryGetValue("sourceId", out var sourceIdObj) ? sourceIdObj?.ToString() : null,
                Page = ParseInt(result.Document.TryGetValue("page", out var pageObj) ? pageObj : null),
                Score = result.Score,
                DocumentVersion = result.Document.TryGetValue("documentVersion", out var versionObj) ? versionObj?.ToString() : null,
                IngestionTimestamp = ParseDateTimeOffset(result.Document.TryGetValue("ingestionTimestamp", out var ingestObj) ? ingestObj : null),
                Checksum = result.Document.TryGetValue("checksum", out var checksumObj) ? checksumObj?.ToString() : null,
                MatterId = result.Document.TryGetValue("matterId", out var matterObj) ? matterObj?.ToString() : null,
                PracticeArea = result.Document.TryGetValue("practiceArea", out var practiceObj) ? practiceObj?.ToString() : null,
                Client = result.Document.TryGetValue("client", out var clientObj) ? clientObj?.ToString() : null,
                ConfidentialityLevel = result.Document.TryGetValue("confidentialityLevel", out var confObj) ? confObj?.ToString() : null,
                AccessGroup = result.Document.TryGetValue("accessGroup", out var groupObj) ? groupObj?.ToString() : null,
                DocumentType = result.Document.TryGetValue("documentType", out var typeObj) ? typeObj?.ToString() : null
            });
        }

        return chunks;
    }

    private string BuildSearchFilter(AskRequestDto request, UserClaimsContext userClaims)
    {
        var baseFilters = new List<string>
        {
            _authorizationFilter.BuildSecurityFilter(userClaims),
            $"matterId eq '{EscapeODataString(request.MatterId)}'"
        };

        var aclFilter = _authorizationFilter.BuildAclFilter(userClaims);
        return _aclFilterEnabled && !string.IsNullOrWhiteSpace(aclFilter)
            ? $"{string.Join(" and ", baseFilters)} and {aclFilter}"
            : string.Join(" and ", baseFilters);
    }

    private static AskSourceDto ToSourceMetadata(RetrievedChunk c) => new()
    {
        SourceFile = c.SourceFile ?? "unknown",
        SourceId = c.SourceId ?? c.DocumentId ?? "unknown",
        MatterId = c.MatterId ?? "unknown",
        DocumentType = c.DocumentType ?? "unknown"
    };

    private static string GetPreferredSourceFile(SearchDocument document)
    {
        var sourceFile = document.TryGetValue("sourceFile", out var sf) ? sf?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(sourceFile)) return sourceFile;
        var source = document.TryGetValue("source", out var s) ? s?.ToString() : null;
        return !string.IsNullOrWhiteSpace(source) ? source : "unknown";
    }

    private static string BuildSnippet(string content, int maxChars) => content.Length <= maxChars ? content : content[..maxChars].TrimEnd() + "...";
    private static int? ParseInt(object? value) => value switch { int i => i, long l when l <= int.MaxValue && l >= int.MinValue => (int)l, _ when int.TryParse(value?.ToString(), out var p) => p, _ => null };
    private static DateTimeOffset? ParseDateTimeOffset(object? value) => value switch { null => null, DateTimeOffset dto => dto, DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero), _ when DateTimeOffset.TryParse(value.ToString(), out var p) => p, _ => null };
    private static string EscapeODataString(string value) => value.Replace("'", "''");
    private IReadOnlyList<string> GetSelectFields() => _baseSelectFields.Where(field => !_missingSelectFields.Contains(field)).ToList();

    private async Task<Response<SearchResults<SearchDocument>>> ExecuteSearchAsync(SearchClient client, string query, SearchOptions options, IReadOnlyList<string> baseFilters, string? aclFilter, CancellationToken cancellationToken)
    {
        while (true)
        {
            var activeFilters = _aclFilterEnabled && !string.IsNullOrEmpty(aclFilter) ? baseFilters.Concat([aclFilter]).ToList() : baseFilters.ToList();
            options.Filter = string.Join(" and ", activeFilters);
            try
            {
                return await client.SearchAsync<SearchDocument>(query, options, cancellationToken);
            }
            catch (RequestFailedException ex) when (TryExtractMissingField(ex, out var field))
            {
                if (_missingSelectFields.Add(field))
                {
                    options.Select.Remove(field);
                    _logger.LogWarning("Search index missing field '{Field}'; removed from select list.", field);
                    continue;
                }
            }
            catch (RequestFailedException ex) when (_aclFilterEnabled && !string.IsNullOrEmpty(aclFilter) && IsAclFilterError(ex))
            {
                _aclFilterEnabled = false;
                _logger.LogWarning("ACL filter fields are missing from the search index. Document-level ACL trimming disabled until restart.");
                continue;
            }
        }
    }

    private static bool TryExtractMissingField(RequestFailedException ex, out string fieldName)
    {
        fieldName = string.Empty;
        var match = Regex.Match(ex.Message, "property '([^']+)' does not exist", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        fieldName = match.Groups[1].Value;
        return !string.IsNullOrWhiteSpace(fieldName);
    }

    private static bool IsAclFilterError(RequestFailedException ex) => ex.Status == 400 && (ex.Message.Contains("allowedUsers", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("allowedGroups", StringComparison.OrdinalIgnoreCase));
}
