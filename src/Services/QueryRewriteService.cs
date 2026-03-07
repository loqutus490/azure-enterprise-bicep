using Azure.AI.OpenAI;

namespace LegalRagApp.Services;

// Rewrites user questions into retrieval-optimized legal search queries.
public sealed class QueryRewriteService : IQueryRewriteService
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly bool _enabled;
    private readonly ILogger<QueryRewriteService> _logger;

    public QueryRewriteService(AzureOpenAIClient openAiClient, IConfiguration configuration, ILogger<QueryRewriteService> logger)
    {
        _logger = logger;
        _enabled = configuration.GetValue<bool?>("Rag:EnableQueryRewrite") ?? true;

        var deployment = configuration["AzureOpenAI:RewriteDeployment"]
            ?? configuration["AzureOpenAI:Deployment"];
        if (string.IsNullOrWhiteSpace(deployment))
            throw new InvalidOperationException("Missing configuration: AzureOpenAI:RewriteDeployment or AzureOpenAI:Deployment");

        _chatClient = openAiClient.GetChatClient(deployment);
    }

    public async Task<string> RewriteAsync(string question, CancellationToken cancellationToken)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(question))
            return question;

        try
        {
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new OpenAI.Chat.SystemChatMessage(
                    "Rewrite legal questions for semantic retrieval. Keep meaning unchanged. Return one concise sentence only."),
                new OpenAI.Chat.UserChatMessage($"Original question: {question}")
            };

            var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
            var rewritten = response.Value.Content.FirstOrDefault()?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(rewritten))
                return question;

            return rewritten;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query rewrite failed. Falling back to original question.");
            return question;
        }
    }
}
