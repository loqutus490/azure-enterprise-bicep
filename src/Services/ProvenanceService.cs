using LegalRagApp.Models;

namespace LegalRagApp.Services;

// Maps citation entries back to retrieved chunk provenance metadata.
public sealed class ProvenanceService : IProvenanceService
{
    public List<CitationDto> EnrichCitations(IReadOnlyList<CitationDto> citations, IReadOnlyList<RetrievedChunk> chunks)
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
            if (!string.IsNullOrWhiteSpace(citation.Document)
                && byDoc.TryGetValue(citation.Document, out var matches)
                && matches.Count > 0)
            {
                RetrievedChunk? matched = null;
                if (citation.Page.HasValue)
                    matched = matches.FirstOrDefault(m => m.Page == citation.Page);
                matched ??= matches[0];

                enriched.Add(new CitationDto
                {
                    DocumentId = string.IsNullOrWhiteSpace(citation.DocumentId)
                        ? matched.DocumentId ?? matched.Checksum ?? string.Empty
                        : citation.DocumentId,
                    Document = citation.Document,
                    Page = citation.Page ?? matched.Page,
                    Excerpt = string.IsNullOrWhiteSpace(citation.Excerpt) ? matched.Snippet : citation.Excerpt,
                    Version = citation.Version ?? matched.DocumentVersion,
                    IngestedAt = citation.IngestedAt ?? matched.IngestionTimestamp,
                    Checksum = citation.Checksum ?? matched.Checksum
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(citation.DocumentId))
                citation.DocumentId = citation.Checksum ?? string.Empty;

            enriched.Add(citation);
        }

        return enriched;
    }
}
