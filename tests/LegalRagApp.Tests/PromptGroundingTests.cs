using LegalRagApp.Models;
using LegalRagApp.Prompts;
using LegalRagApp.Services;

namespace LegalRagApp.Tests;

public class PromptGroundingTests
{
    [Fact]
    public void StructuredLegalAnswer_Template_RequiresStrictGroundingAndDocumentIds()
    {
        var template = PromptTemplates.StructuredLegalAnswer;

        Assert.Contains("Use only Retrieved context", template, StringComparison.Ordinal);
        Assert.Contains("Do not invent citations", template, StringComparison.Ordinal);
        Assert.Contains("\"documentId\": \"string\"", template, StringComparison.Ordinal);
        Assert.Contains(PromptOutputFactory.InsufficientContextSummary, template, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInsufficientContextFallback_ReturnsStructuredLowConfidenceResponse()
    {
        var fallback = PromptOutputFactory.BuildInsufficientContextFallback();

        Assert.Equal(PromptOutputFactory.InsufficientContextSummary, fallback.Summary);
        Assert.Empty(fallback.KeyPoints);
        Assert.Empty(fallback.Citations);
        Assert.Equal("low", fallback.Confidence);
    }

    [Fact]
    public void EnrichCitations_BackfillsDocumentIdFromRetrievedChunks()
    {
        var service = new ProvenanceService();
        var chunks = new List<RetrievedChunk>
        {
            new()
            {
                DocumentId = "doc-nda-001",
                SourceFile = "nda.docx",
                Page = 2,
                Snippet = "Confidentiality survives termination."
            }
        };

        var citations = new List<CitationDto>
        {
            new()
            {
                Document = "nda.docx",
                Page = 2,
                Excerpt = "Confidentiality survives termination."
            }
        };

        var enriched = service.EnrichCitations(citations, chunks);

        Assert.Single(enriched);
        Assert.Equal("doc-nda-001", enriched[0].DocumentId);
    }
}
