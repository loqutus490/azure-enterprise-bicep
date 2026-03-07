using System.Collections.Concurrent;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

// In-memory operational metrics from audit events; can later be backed by persistent analytics storage.
public sealed class MetricsService : IMetricsService
{
    private readonly ConcurrentQueue<AskAuditRecord> _records = new();

    public void TrackQueryAudit(AskAuditRecord record)
    {
        _records.Enqueue(record);

        // Keep bounded memory footprint.
        while (_records.Count > 5000 && _records.TryDequeue(out _))
        {
        }
    }

    public MetricsSnapshotDto GetSnapshot(DateTimeOffset nowUtc)
    {
        var today = nowUtc.UtcDateTime.Date;
        var todayRecords = _records
            .Where(r => r.Timestamp.UtcDateTime.Date == today)
            .ToList();

        if (todayRecords.Count == 0)
        {
            return new MetricsSnapshotDto
            {
                QueriesToday = 0,
                AvgResponseTimeMs = 0,
                AvgConfidence = "low",
                MostCommonQueries = new List<string>(),
                DocumentsAccessed = new List<string>()
            };
        }

        var avgResponseTime = (long)Math.Round(todayRecords.Average(r => r.ResponseTimeMs));
        var avgConfidence = CalculateAggregateConfidence(todayRecords.Select(r => r.Confidence));

        var mostCommonQueries = todayRecords
            .GroupBy(r => NormalizeQuery(r.RewrittenQuery.Length > 0 ? r.RewrittenQuery : r.Question), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(g => g.Key)
            .ToList();

        var documentsAccessed = todayRecords
            .SelectMany(r => r.DocumentsRetrieved)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new MetricsSnapshotDto
        {
            QueriesToday = todayRecords.Count,
            AvgResponseTimeMs = avgResponseTime,
            AvgConfidence = avgConfidence,
            MostCommonQueries = mostCommonQueries,
            DocumentsAccessed = documentsAccessed
        };
    }

    private static string NormalizeQuery(string query)
    {
        return (query ?? string.Empty).Trim();
    }

    private static string CalculateAggregateConfidence(IEnumerable<string> confidences)
    {
        var values = confidences.ToList();
        if (values.Count == 0)
            return "low";

        var score = values.Average(c => c.ToLowerInvariant() switch
        {
            "high" => 3,
            "medium" => 2,
            _ => 1
        });

        return score >= 2.5 ? "high" : score >= 1.75 ? "medium" : "low";
    }
}
