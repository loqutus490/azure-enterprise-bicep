using System.Text.Json;
using Azure.AI.OpenAI;
using LegalRagApp.Models;
using LegalRagApp.Prompts;

namespace LegalRagApp.Services;

public sealed class ChatService : IChatService
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly ILogger<ChatService> _logger;
    private readonly Dictionary<string, double> _pricingPer1kTokens;
    private readonly double _defaultCostPer1kTokens;
    private readonly IPromptBuilder _promptBuilder;

    public ChatService(AzureOpenAIClient openAiClient, IConfiguration configuration, IPromptBuilder promptBuilder, ILogger<ChatService> logger)
    {
        var chatDeployment = configuration["AzureOpenAI:Deployment"];
        if (string.IsNullOrWhiteSpace(chatDeployment))
            throw new InvalidOperationException("Missing configuration: AzureOpenAI:Deployment");

        _chatClient = openAiClient.GetChatClient(chatDeployment);
        ModelUsed = chatDeployment;
        _logger = logger;
        _promptBuilder = promptBuilder;
        _defaultCostPer1kTokens = configuration.GetValue<double?>("Costing:DefaultUsdPer1kTokens") ?? 0.005d;
        _pricingPer1kTokens = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o"] = 0.01,
            ["gpt-4o-mini"] = 0.0025,
            ["gpt-4.1-mini"] = 0.0025
        };
    }

    public string ModelUsed { get; }

    public async Task<ChatGenerationResult> GenerateStructuredAnswerAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var prompt = _promptBuilder.BuildPrompt(request.Question, request.RetrievedChunks, request.ConversationHistory);

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new OpenAI.Chat.SystemChatMessage(prompt.SystemPrompt),
            new OpenAI.Chat.UserChatMessage(prompt.UserPrompt)
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var modelText = response.Value.Content.FirstOrDefault()?.Text;
        var (promptTokens, completionTokens) = ExtractTokenUsage(response.Value);
        var estimatedCost = EstimateCost(promptTokens, completionTokens);

        if (string.IsNullOrWhiteSpace(modelText))
            return new ChatGenerationResult
            {
                Answer = BuildFallback(PromptOutputFactory.InsufficientContextSummary, request.RetrievedChunks),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                EstimatedCost = estimatedCost
            };

        var parsed = TryParseStructuredJson(modelText);
        if (parsed is null)
        {
            _logger.LogWarning("Model response was not valid JSON. Falling back to plain summary output.");
            return new ChatGenerationResult
            {
                Answer = BuildFallback(modelText, request.RetrievedChunks),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                EstimatedCost = estimatedCost
            };
        }

        if (string.IsNullOrWhiteSpace(parsed.Summary))
            parsed.Summary = PromptOutputFactory.InsufficientContextSummary;

        if (parsed.Citations.Count == 0 && request.RetrievedChunks.Count > 0)
        {
            // Preserve citations behavior even when model omits the field.
            parsed.Citations = request.RetrievedChunks.Take(2).Select(c => new CitationDto
            {
                DocumentId = c.DocumentId ?? c.Checksum ?? string.Empty,
                Document = c.SourceFile ?? "unknown",
                Page = c.Page,
                Excerpt = c.Snippet,
                Version = c.DocumentVersion,
                IngestedAt = c.IngestionTimestamp,
                Checksum = c.Checksum
            }).ToList();
        }
        else
        {
            parsed.Citations = BackfillCitationDocumentIds(parsed.Citations, request.RetrievedChunks);
        }

        if (string.IsNullOrWhiteSpace(parsed.Confidence))
            parsed.Confidence = InferConfidence(parsed, request.RetrievedChunks.Count);

        return new ChatGenerationResult
        {
            Answer = parsed,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            EstimatedCost = estimatedCost
        };
    }


    private static StructuredAnswerDto? TryParseStructuredJson(string raw)
    {
        try
        {
            var json = ExtractJsonObject(raw);
            var dto = JsonSerializer.Deserialize<StructuredAnswerDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (dto is null)
                return null;

            dto.KeyPoints ??= new List<string>();
            dto.Citations ??= new List<CitationDto>();
            return dto;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("No JSON object found in model response.");

        return raw[start..(end + 1)];
    }

    private static StructuredAnswerDto BuildFallback(string summary, IReadOnlyList<RetrievedChunk> chunks)
    {
        return new StructuredAnswerDto
        {
            Summary = summary,
            KeyPoints = new List<string>(),
            Citations = chunks.Take(2).Select(c => new CitationDto
            {
                DocumentId = c.DocumentId ?? c.Checksum ?? string.Empty,
                Document = c.SourceFile ?? "unknown",
                Page = c.Page,
                Excerpt = c.Snippet,
                Version = c.DocumentVersion,
                IngestedAt = c.IngestionTimestamp,
                Checksum = c.Checksum
            }).ToList(),
            Confidence = InferConfidence(summary, chunks.Count)
        };
    }

    private static List<CitationDto> BackfillCitationDocumentIds(IReadOnlyList<CitationDto> citations, IReadOnlyList<RetrievedChunk> chunks)
    {
        if (citations.Count == 0)
            return new List<CitationDto>();

        var byDoc = chunks
            .Where(c => !string.IsNullOrWhiteSpace(c.SourceFile))
            .GroupBy(c => c.SourceFile!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var enriched = new List<CitationDto>(citations.Count);
        foreach (var citation in citations)
        {
            if (!string.IsNullOrWhiteSpace(citation.DocumentId))
            {
                enriched.Add(citation);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(citation.Document)
                && byDoc.TryGetValue(citation.Document, out var matches)
                && matches.Count > 0)
            {
                var matched = citation.Page.HasValue
                    ? matches.FirstOrDefault(m => m.Page == citation.Page) ?? matches[0]
                    : matches[0];

                citation.DocumentId = matched.DocumentId ?? matched.Checksum ?? string.Empty;
            }

            enriched.Add(citation);
        }

        return enriched;
    }

    private static string InferConfidence(StructuredAnswerDto answer, int chunkCount) => InferConfidence(answer.Summary, chunkCount);

    private static string InferConfidence(string summary, int chunkCount)
    {
        if (chunkCount == 0)
            return "low";

        if (summary.Contains(PromptOutputFactory.InsufficientContextSummary, StringComparison.OrdinalIgnoreCase))
            return "low";

        return chunkCount >= 3 ? "high" : "medium";
    }

    private (int promptTokens, int completionTokens) ExtractTokenUsage(object completionResult)
    {
        try
        {
            var usageProp = completionResult.GetType().GetProperty("Usage");
            var usageObj = usageProp?.GetValue(completionResult);
            if (usageObj is null)
                return (0, 0);

            var promptProp = usageObj.GetType().GetProperty("InputTokenCount")
                ?? usageObj.GetType().GetProperty("PromptTokens");
            var completionProp = usageObj.GetType().GetProperty("OutputTokenCount")
                ?? usageObj.GetType().GetProperty("CompletionTokens");

            var prompt = Convert.ToInt32(promptProp?.GetValue(usageObj) ?? 0);
            var completion = Convert.ToInt32(completionProp?.GetValue(usageObj) ?? 0);
            return (prompt, completion);
        }
        catch
        {
            return (0, 0);
        }
    }

    private double EstimateCost(int promptTokens, int completionTokens)
    {
        var totalTokens = promptTokens + completionTokens;
        if (totalTokens <= 0)
            return 0.0;

        var rate = _pricingPer1kTokens.TryGetValue(ModelUsed, out var configuredRate)
            ? configuredRate
            : _defaultCostPer1kTokens;
        return Math.Round((totalTokens / 1000d) * rate, 6);
    }
}
