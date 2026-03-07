using Azure.AI.OpenAI;
using Azure.Search.Documents;
using LegalRagApp.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace LegalRagApp.Services;

public sealed class HealthCheckService : IHealthCheckService
{
    private readonly IIndexVersionService _indexVersionService;
    private readonly AzureOpenAIClient _openAiClient;
    private readonly IDistributedCache _cache;
    private readonly string _chatDeployment;

    public HealthCheckService(
        IIndexVersionService indexVersionService,
        AzureOpenAIClient openAiClient,
        IDistributedCache cache,
        IConfiguration configuration)
    {
        _indexVersionService = indexVersionService;
        _openAiClient = openAiClient;
        _cache = cache;
        _chatDeployment = configuration["AzureOpenAI:Deployment"] ?? string.Empty;
    }

    public async Task<HealthStatusDto> GetHealthAsync(CancellationToken cancellationToken)
    {
        var activeIndex = _indexVersionService.GetActiveIndex();
        var openAiStatus = await CheckOpenAiAsync(cancellationToken);
        var searchStatus = await CheckSearchAsync(cancellationToken);
        var redisStatus = await CheckRedisAsync(cancellationToken);

        var overall = openAiStatus == "connected" && searchStatus == "connected" && redisStatus == "connected"
            ? "healthy"
            : "degraded";

        return new HealthStatusDto
        {
            Status = overall,
            VectorIndex = activeIndex,
            OpenAI = openAiStatus,
            SearchService = searchStatus,
            Redis = redisStatus
        };
    }

    private async Task<string> CheckOpenAiAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_chatDeployment))
            return "disconnected";

        try
        {
            var chatClient = _openAiClient.GetChatClient(_chatDeployment);
            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new OpenAI.Chat.SystemChatMessage("health-check"),
                new OpenAI.Chat.UserChatMessage("ping")
            };
            await chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
            return "connected";
        }
        catch
        {
            return "disconnected";
        }
    }

    private async Task<string> CheckSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            SearchClient searchClient = _indexVersionService.CreateSearchClientForActiveIndex();
            var response = await searchClient.GetDocumentCountAsync(cancellationToken);
            _ = response.Value;
            return "connected";
        }
        catch
        {
            return "disconnected";
        }
    }

    private async Task<string> CheckRedisAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = $"health:ping:{Guid.NewGuid():N}";
            await _cache.SetStringAsync(key, "1", new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
            }, cancellationToken);

            var value = await _cache.GetStringAsync(key, cancellationToken);
            return value == "1" ? "connected" : "disconnected";
        }
        catch
        {
            return "disconnected";
        }
    }
}
