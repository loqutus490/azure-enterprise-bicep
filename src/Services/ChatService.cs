using System.Text;
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

    public ChatService(AzureOpenAIClient openAiClient, IConfiguration configuration, ILogger<ChatService> logger)
    {
        var chatDeployment = configuration["AzureOpenAI:Deployment"];
        if (string.IsNullOrWhiteSpace(chatDeployment))
            throw new InvalidOperationException("Missing configuration: AzureOpenAI:Deployment");

        _chatClient = openAiClient.GetChatClient(chatDeployment);
        ModelUsed = chatDeployment;
        _logger = logger;
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
        var contextBlock = BuildContextBlock(request.RetrievedChunks);
        var historyBlock = BuildHistoryBlock(request.ConversationHistory);

        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new OpenAI.Chat.SystemChatMessage(PromptTemplates.StructuredLegalAnswer),
            new OpenAI.Chat.UserChatMessage($"Conversation history:\n{historyBlock}\n\nRetrieved context:\n{contextBlock}\n\nQuestion:\n{request.Question}")
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        var modelText = response.Value.Content.FirstOrDefault()?.Text;
        var (promptTokens, completionTokens) = ExtractTokenUsage(response.Value);
        var estimatedCost = EstimateCost(promptTokens, completionTokens);

        if (string.IsNullOrWhiteSpace(modelText))
            return new ChatGenerationResult
            {
                Answer = BuildFallback("I cannot find this information in the provided materials.", request.RetrievedChunks),
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
            parsed.Summary = "I cannot find this information in the provided materials.";

        if (parsed.Citations.Count == 0 && request.RetrievedChunks.Count > 0)
        {
            // Preserve citations behavior even when model omits the field.
            parsed.Citations = request.RetrievedChunks.Take(2).Select(c => new CitationDto
            {
                Document = c.SourceFile ?? "unknown",
                Page = c.Page,
                Excerpt = c.Snippet,
                Version = c.DocumentVersion,
                IngestedAt = c.IngestionTimestamp,
                Checksum = c.Checksum
            }).ToList();
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

    private static string BuildContextBlock(IReadOnlyList<RetrievedChunk> chunks)
    {
        if (chunks.Count == 0)
            return "No retrieved documents.";

        var sb = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            sb.AppendLine($"[Chunk {i + 1}]");
            sb.AppendLine($"Document: {chunk.SourceFile ?? "unknown"}");
            sb.AppendLine($"Page: {(chunk.Page?.ToString() ?? "unknown")}");
            sb.AppendLine($"Version: {chunk.DocumentVersion ?? "unknown"}");
            sb.AppendLine($"IngestedAt: {(chunk.IngestionTimestamp?.ToString("o") ?? "unknown")}");
            sb.AppendLine($"Excerpt: {chunk.Snippet}");
            sb.AppendLine($"Content: {chunk.Content}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildHistoryBlock(IReadOnlyList<ConversationMessage> history)
    {
        if (history.Count == 0)
            return "(none)";

        var lines = history.Select(m => $"{m.Role}: {m.Content}");
        return string.Join('\n', lines);
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

    private static string InferConfidence(StructuredAnswerDto answer, int chunkCount) => InferConfidence(answer.Summary, chunkCount);

    private static string InferConfidence(string summary, int chunkCount)
    {
        if (chunkCount == 0)
            return "low";

        if (summary.Contains("I cannot find this information", StringComparison.OrdinalIgnoreCase))
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
