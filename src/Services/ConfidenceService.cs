using LegalRagApp.Models;

namespace LegalRagApp.Services;

// Computes confidence based on retrieval volume, semantic score quality, and citation consistency.
public sealed class ConfidenceService : IConfidenceService
{
    public string CalculateConfidence(IReadOnlyList<RetrievedChunk> chunks, double averageScore, IReadOnlyList<CitationDto> citations)
    {
        var docsRetrieved = chunks
            .Select(c => c.SourceFile)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var citationConsistency = ComputeCitationConsistency(chunks, citations);

        if (docsRetrieved >= 3 && averageScore >= 0.80 && citationConsistency >= 0.70)
            return "high";

        if (docsRetrieved >= 2 && averageScore >= 0.65 && citationConsistency >= 0.50)
            return "medium";

        return "low";
    }

    private static double ComputeCitationConsistency(IReadOnlyList<RetrievedChunk> chunks, IReadOnlyList<CitationDto> citations)
    {
        if (citations.Count == 0)
            return 0.0;

        var validDocIds = new HashSet<string>(
            chunks.Select(c => c.DocumentId ?? c.Checksum).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>(),
            StringComparer.OrdinalIgnoreCase);

        var validDocs = new HashSet<string>(
            chunks.Select(c => c.SourceFile).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>(),
            StringComparer.OrdinalIgnoreCase);

        if (validDocs.Count == 0 && validDocIds.Count == 0)
            return 0.0;

        var consistent = citations.Count(c =>
            (!string.IsNullOrWhiteSpace(c.DocumentId) && validDocIds.Contains(c.DocumentId))
            || (!string.IsNullOrWhiteSpace(c.Document) && validDocs.Contains(c.Document)));
        return (double)consistent / citations.Count;
    }
}
