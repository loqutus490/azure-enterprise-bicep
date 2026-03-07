using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

// Handles safe removal of document chunks from active index and records lineage deletion events.
public sealed class ReindexService : IReindexService
{
    private readonly IIndexVersionService _indexVersionService;
    private readonly ILineageService _lineageService;

    public ReindexService(IIndexVersionService indexVersionService, ILineageService lineageService)
    {
        _indexVersionService = indexVersionService;
        _lineageService = lineageService;
    }

    public async Task<int> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            return 0;

        var searchClient = _indexVersionService.CreateSearchClientForActiveIndex();
        var escaped = documentId.Replace("'", "''", StringComparison.Ordinal);

        var options = new SearchOptions
        {
            Size = 1000,
            Filter = $"sourceFile eq '{escaped}' or source eq '{escaped}' or documentId eq '{escaped}'"
        };
        options.Select.Add("id");
        options.Select.Add("sourceFile");
        options.Select.Add("checksum");

        var search = await searchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
        var ids = new List<string>();
        string checksum = string.Empty;
        string sourceFile = documentId;

        await foreach (var result in search.Value.GetResultsAsync())
        {
            var id = result.Document.TryGetValue("id", out var idObj) ? idObj?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);

            if (string.IsNullOrWhiteSpace(checksum)
                && result.Document.TryGetValue("checksum", out var checksumObj)
                && checksumObj is not null)
            {
                checksum = checksumObj.ToString() ?? string.Empty;
            }

            if (result.Document.TryGetValue("sourceFile", out var sfObj) && sfObj is not null)
                sourceFile = sfObj.ToString() ?? sourceFile;
        }

        if (ids.Count == 0)
            return 0;

        await searchClient.DeleteDocumentsAsync("id", ids, cancellationToken: cancellationToken);

        await _lineageService.AppendAsync(new DocumentLineageRecord
        {
            DocumentId = documentId,
            SourceFile = sourceFile,
            Checksum = checksum,
            IngestionTimestamp = DateTimeOffset.UtcNow,
            IndexedChunks = ids.Count,
            IndexVersion = _indexVersionService.GetActiveIndex(),
            EventType = "delete"
        }, cancellationToken);

        return ids.Count;
    }
}
