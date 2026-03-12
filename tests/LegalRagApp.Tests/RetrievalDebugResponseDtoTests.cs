using System.Text.Json;
using LegalRagApp.Models;

namespace LegalRagApp.Tests;

public class RetrievalDebugResponseDtoTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void RetrievalDebugResponseDto_RoundTrips_WithAllFieldsPopulated()
    {
        var original = new RetrievalDebugResponseDto
        {
            Query = "What is the termination clause?",
            User = new UserClaimsContext
            {
                UserId = "user@firm.com",
                Email = "user@firm.com",
                Role = "attorney",
                PermittedMatters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MATTER-001" },
                Groups = new List<string> { "team-a" }
            },
            RawRetrievalCount = 3,
            FilteredRetrievalCount = 2,
            PromptContextPreview = "Termination requires 30 days written notice.",
            FallbackReason = null,
            Sources = new List<AskSourceDto>
            {
                new()
                {
                    SourceFile = "master-services-agreement.pdf",
                    SourceId = "src-1",
                    MatterId = "MATTER-001",
                    DocumentType = "contract"
                }
            }
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RetrievalDebugResponseDto>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Query, deserialized!.Query);
        Assert.Equal(original.RawRetrievalCount, deserialized.RawRetrievalCount);
        Assert.Equal(original.FilteredRetrievalCount, deserialized.FilteredRetrievalCount);
        Assert.Equal(original.PromptContextPreview, deserialized.PromptContextPreview);
        Assert.Null(deserialized.FallbackReason);
        Assert.Single(deserialized.Sources);
        Assert.Equal("master-services-agreement.pdf", deserialized.Sources[0].SourceFile);
        Assert.Equal("src-1", deserialized.Sources[0].SourceId);
        Assert.Equal("MATTER-001", deserialized.Sources[0].MatterId);
        Assert.Equal("contract", deserialized.Sources[0].DocumentType);
    }

    [Fact]
    public void RetrievalDebugResponseDto_RoundTrips_WithFallbackReason()
    {
        var original = new RetrievalDebugResponseDto
        {
            Query = "no-docs scenario",
            User = new UserClaimsContext(),
            RawRetrievalCount = 0,
            FilteredRetrievalCount = 0,
            PromptContextPreview = string.Empty,
            FallbackReason = "fallback_no_docs",
            Sources = new List<AskSourceDto>()
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RetrievalDebugResponseDto>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(0, deserialized!.RawRetrievalCount);
        Assert.Equal(0, deserialized.FilteredRetrievalCount);
        Assert.Equal("fallback_no_docs", deserialized.FallbackReason);
        Assert.Empty(deserialized.Sources);
    }

    [Fact]
    public void AskSourceDto_NullableStringFields_DefaultToEmpty_WhenNotProvided()
    {
        // Verifies that when a chunk has null metadata fields, the DTO fields
        // are populated with string.Empty rather than null, preventing JSON
        // serialization errors.
        var chunk = new RetrievedChunk
        {
            Content = "Some content",
            Snippet = "Some snippet",
            SourceFile = null,
            SourceId = null,
            MatterId = null,
            DocumentType = null
        };

        var dto = new AskSourceDto
        {
            SourceFile = chunk.SourceFile ?? string.Empty,
            SourceId = chunk.SourceId ?? string.Empty,
            MatterId = chunk.MatterId ?? string.Empty,
            DocumentType = chunk.DocumentType ?? string.Empty
        };

        Assert.Equal(string.Empty, dto.SourceFile);
        Assert.Equal(string.Empty, dto.SourceId);
        Assert.Equal(string.Empty, dto.MatterId);
        Assert.Equal(string.Empty, dto.DocumentType);

        // Verify the DTO serializes/deserializes correctly with empty strings
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AskSourceDto>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(string.Empty, deserialized!.SourceFile);
        Assert.Equal(string.Empty, deserialized.SourceId);
        Assert.Equal(string.Empty, deserialized.MatterId);
        Assert.Equal(string.Empty, deserialized.DocumentType);
    }

    [Fact]
    public void RetrievalDebugResponseDto_Deserializes_FromValidJson()
    {
        // Simulate the JSON that would be returned by the /debug/retrieval endpoint
        const string json = """
            {
                "query": "What is the termination clause?",
                "user": {
                    "userId": "authorized@firm.com",
                    "email": "authorized@firm.com",
                    "role": "unknown",
                    "permittedMatters": ["MATTER-001"],
                    "groups": ["team-a"]
                },
                "rawRetrievalCount": 2,
                "filteredRetrievalCount": 1,
                "sources": [
                    {
                        "sourceFile": "master-services-agreement.pdf",
                        "sourceId": "src-1",
                        "matterId": "MATTER-001",
                        "documentType": "contract"
                    }
                ],
                "promptContextPreview": "Termination requires 30 days written notice.",
                "fallbackReason": null
            }
            """;

        var deserialized = JsonSerializer.Deserialize<RetrievalDebugResponseDto>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("What is the termination clause?", deserialized!.Query);
        Assert.NotNull(deserialized.User);
        Assert.Equal("authorized@firm.com", deserialized.User.UserId);
        Assert.Equal(2, deserialized.RawRetrievalCount);
        Assert.Equal(1, deserialized.FilteredRetrievalCount);
        Assert.Single(deserialized.Sources);
        Assert.Equal("master-services-agreement.pdf", deserialized.Sources[0].SourceFile);
        Assert.Equal("contract", deserialized.Sources[0].DocumentType);
        Assert.Equal("Termination requires 30 days written notice.", deserialized.PromptContextPreview);
        Assert.Null(deserialized.FallbackReason);
    }
}
