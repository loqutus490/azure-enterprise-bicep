using System.Text;
using LegalRagApp.Models;
using LegalRagApp.Prompts;

namespace LegalRagApp.Services;

public sealed class PromptBuilder : IPromptBuilder
{
    public PromptContext BuildPrompt(string question, IReadOnlyList<RetrievedChunk> chunks, IReadOnlyList<ConversationMessage> history)
    {
        var contextBlock = BuildContextBlock(chunks);
        var historyBlock = BuildHistoryBlock(history);
        var userPrompt = $"Conversation history:\n{historyBlock}\n\nRetrieved context:\n{contextBlock}\n\nQuestion:\n{question}";

        return new PromptContext
        {
            SystemPrompt = PromptTemplates.StructuredLegalAnswer,
            UserPrompt = userPrompt,
            ContextPreview = BuildPreview(chunks)
        };
    }

    private static string BuildContextBlock(IReadOnlyList<RetrievedChunk> chunks)
    {
        if (chunks.Count == 0)
            return "No authorized retrieved documents.";

        var sb = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            sb.AppendLine($"[Chunk {i + 1}]");
            sb.AppendLine($"SourceFile: {chunk.SourceFile ?? "unknown"}");
            sb.AppendLine($"SourceId: {chunk.SourceId ?? chunk.DocumentId ?? "unknown"}");
            sb.AppendLine($"MatterId: {chunk.MatterId ?? "unknown"}");
            sb.AppendLine($"DocumentType: {chunk.DocumentType ?? "unknown"}");
            sb.AppendLine($"Page: {(chunk.Page?.ToString() ?? "unknown")}");
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

    private static string BuildPreview(IReadOnlyList<RetrievedChunk> chunks)
    {
        return string.Join("\n", chunks.Take(3).Select(c =>
            $"{c.SourceFile ?? "unknown"} ({c.SourceId ?? c.DocumentId ?? "unknown"}/{c.MatterId ?? "unknown"}/{c.DocumentType ?? "unknown"}): {c.Snippet}"));
    }
}
