using LegalRagApp.Models;

namespace LegalRagApp.Prompts;

public static class PromptOutputFactory
{
    public const string InsufficientContextSummary = "I cannot find this information in the provided materials.";

    public static StructuredAnswerDto BuildInsufficientContextFallback()
    {
        return new StructuredAnswerDto
        {
            Summary = InsufficientContextSummary,
            KeyPoints = new List<string>(),
            Citations = new List<CitationDto>(),
            Confidence = "low"
        };
    }
}
