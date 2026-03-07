using System.Collections.Concurrent;
using System.Text.Json;
using LegalRagApp.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace LegalRagApp.Services;

public sealed class RedisMemoryService : IMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisMemoryService> _logger;
    private readonly string _keyPrefix;
    private readonly int _cacheTtlMinutes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public RedisMemoryService(IDistributedCache cache, IConfiguration configuration, ILogger<RedisMemoryService> logger)
    {
        _cache = cache;
        _logger = logger;
        _keyPrefix = configuration["Memory:Redis:KeyPrefix"] ?? "legalrag:conversation:";
        _cacheTtlMinutes = configuration.GetValue<int?>("Memory:Redis:TtlMinutes") ?? 1440;
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(string conversationId, int maxMessages, CancellationToken cancellationToken)
    {
        var key = BuildKey(conversationId);
        var raw = await _cache.GetStringAsync(key, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<ConversationMessage>();

        ConversationMemory? memory;
        try
        {
            memory = JsonSerializer.Deserialize<ConversationMemory>(raw, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize conversation memory for {ConversationId}", conversationId);
            return Array.Empty<ConversationMessage>();
        }

        if (memory?.Messages is null || memory.Messages.Count == 0)
            return Array.Empty<ConversationMessage>();

        return memory.Messages
            .OrderBy(m => m.TimestampUtc)
            .TakeLast(Math.Max(maxMessages, 1))
            .ToList();
    }

    public async Task AppendMessagesAsync(string conversationId, IEnumerable<ConversationMessage> messages, int maxMessages, CancellationToken cancellationToken)
    {
        var key = BuildKey(conversationId);
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetMemoryAsync(key, cancellationToken);
            existing.Messages.AddRange(messages);
            existing.Messages = existing.Messages
                .OrderBy(m => m.TimestampUtc)
                .TakeLast(Math.Max(maxMessages, 1))
                .ToList();

            var payload = JsonSerializer.Serialize(existing, JsonOptions);
            await _cache.SetStringAsync(
                key,
                payload,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_cacheTtlMinutes)
                },
                cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<ConversationMemory> GetMemoryAsync(string key, CancellationToken cancellationToken)
    {
        var raw = await _cache.GetStringAsync(key, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ConversationMemory
            {
                ConversationId = key.Replace(_keyPrefix, string.Empty, StringComparison.OrdinalIgnoreCase),
                Messages = new List<ConversationMessage>()
            };
        }

        try
        {
            return JsonSerializer.Deserialize<ConversationMemory>(raw, JsonOptions)
                ?? new ConversationMemory
                {
                    ConversationId = key.Replace(_keyPrefix, string.Empty, StringComparison.OrdinalIgnoreCase),
                    Messages = new List<ConversationMessage>()
                };
        }
        catch
        {
            return new ConversationMemory
            {
                ConversationId = key.Replace(_keyPrefix, string.Empty, StringComparison.OrdinalIgnoreCase),
                Messages = new List<ConversationMessage>()
            };
        }
    }

    private string BuildKey(string conversationId) => _keyPrefix + conversationId;
}
