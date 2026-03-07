using System.Text.Json;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

// Stores lineage records in JSONL structured storage for auditing and replay.
public sealed class LineageService : ILineageService
{
    private readonly string _lineageFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LineageService(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["Lineage:Path"] ?? "data/lineage-records.jsonl";
        _lineageFilePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);
        var dataDir = Path.GetDirectoryName(_lineageFilePath) ?? environment.ContentRootPath;
        Directory.CreateDirectory(dataDir);
        if (!File.Exists(_lineageFilePath))
            File.WriteAllText(_lineageFilePath, string.Empty);
    }

    public async Task<IReadOnlyList<DocumentLineageRecord>> GetLineageAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var lines = await File.ReadAllLinesAsync(_lineageFilePath, cancellationToken);
            var records = new List<DocumentLineageRecord>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var record = JsonSerializer.Deserialize<DocumentLineageRecord>(line, JsonOptions);
                    if (record is not null)
                        records.Add(record);
                }
                catch
                {
                    // Skip malformed entries and continue.
                }
            }

            return records
                .OrderByDescending(r => r.IngestionTimestamp)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(DocumentLineageRecord record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_lineageFilePath, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
