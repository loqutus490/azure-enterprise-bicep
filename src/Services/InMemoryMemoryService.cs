using System.Collections.Concurrent;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

// In-memory implementation. Swap with Redis-backed implementation behind IMemoryService when needed.
public sealed class InMemoryMemoryService : IMemoryService
{
    private readonly ConcurrentDictionary<string, ConversationMemory> _store = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(string conversationId, int maxMessages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_store.TryGetValue(conversationId, out var memory) || memory.Messages.Count == 0)
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(Array.Empty<ConversationMessage>());

        var result = memory.Messages
            .OrderBy(m => m.TimestampUtc)
            .TakeLast(Math.Max(maxMessages, 1))
            .ToList();

        return Task.FromResult<IReadOnlyList<ConversationMessage>>(result);
    }

    public Task AppendMessagesAsync(string conversationId, IEnumerable<ConversationMessage> messages, int maxMessages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var memory = _store.GetOrAdd(conversationId, key => new ConversationMemory { ConversationId = key });
        lock (memory)
        {
            memory.Messages.AddRange(messages);
            if (memory.Messages.Count > maxMessages)
            {
                var keep = memory.Messages
                    .OrderBy(m => m.TimestampUtc)
                    .TakeLast(maxMessages)
                    .ToList();
                memory.Messages = keep;
            }
        }

        return Task.CompletedTask;
    }
}
