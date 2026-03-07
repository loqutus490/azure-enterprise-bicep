using System.Text;
using System.Text.Json;
using LegalRagApp.Models;
using LegalRagApp.Services;

namespace LegalRagApp.Middleware;

public sealed class PromptSecurityMiddleware
{
    private readonly RequestDelegate _next;

    public PromptSecurityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IPromptSecurityService promptSecurityService)
    {
        if (!context.Request.Path.Equals("/ask", StringComparison.OrdinalIgnoreCase)
            || !HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context);
            return;
        }

        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;
        }

        if (!TryExtractQuestion(body, out var question))
        {
            await _next(context);
            return;
        }

        var securityResult = promptSecurityService.AnalyzePrompt(question ?? string.Empty);
        if (!securityResult.IsAllowed)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Prompt rejected due to security policy.\"}", context.RequestAborted);
            return;
        }

        // Surface sanitized question to controller without mutating original request body contract.
        context.Items["SanitizedQuestion"] = securityResult.SanitizedPrompt;
        await _next(context);
    }

    private static bool TryExtractQuestion(string requestBody, out string? question)
    {
        question = null;
        if (string.IsNullOrWhiteSpace(requestBody))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (!doc.RootElement.TryGetProperty("question", out var qElement))
                return false;

            question = qElement.GetString();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
