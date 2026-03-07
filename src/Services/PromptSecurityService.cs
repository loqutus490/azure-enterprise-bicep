using System.Text.RegularExpressions;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

// Detects common prompt-injection and data-exfiltration patterns before RAG execution.
public sealed class PromptSecurityService : IPromptSecurityService
{
    private static readonly (Regex Pattern, string Reason)[] BlockRules =
    {
        (new Regex(@"\bignore\s+(all\s+)?(previous|prior|system)\s+instructions\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Instruction override attempt"),
        (new Regex(@"\breveal\b.*\b(system\s+prompt|hidden\s+prompt|confidential\s+data|secrets?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Attempt to reveal hidden data or prompt"),
        (new Regex(@"\blist\s+(every|all)\s+documents?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Bulk data extraction attempt"),
        (new Regex(@"\b(disregard|bypass|disable)\b.*\b(safety|policy|guardrails?|rbac|authorization)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Safety bypass attempt"),
        (new Regex(@"\bprint\s+the\s+entire\s+(database|index)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Database exfiltration attempt")
    };

    public PromptSecurityResult AnalyzePrompt(string input)
    {
        var prompt = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new PromptSecurityResult
            {
                IsAllowed = false,
                Reason = "Empty prompt",
                SanitizedPrompt = string.Empty
            };
        }

        foreach (var (pattern, reason) in BlockRules)
        {
            if (pattern.IsMatch(prompt))
            {
                return new PromptSecurityResult
                {
                    IsAllowed = false,
                    Reason = reason,
                    SanitizedPrompt = string.Empty
                };
            }
        }

        // Basic sanitization for prompt delimiters used in injection attempts.
        var sanitized = prompt
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Replace("<system>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</system>", string.Empty, StringComparison.OrdinalIgnoreCase);

        return new PromptSecurityResult
        {
            IsAllowed = true,
            SanitizedPrompt = sanitized
        };
    }
}
