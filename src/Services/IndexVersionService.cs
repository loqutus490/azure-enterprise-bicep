using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using LegalRagApp.Models;

namespace LegalRagApp.Services;

// Manages active vector index pointer and supports index version switching/rollback.
public sealed class IndexVersionService : IIndexVersionService
{
    private readonly string _searchEndpoint;
    private readonly string? _searchApiKey;
    private readonly string _stateFilePath;
    private readonly object _gate = new();
    private readonly ILogger<IndexVersionService> _logger;
    private IndexVersionState _state;

    public IndexVersionService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<IndexVersionService> logger)
    {
        _logger = logger;
        _searchEndpoint = configuration["AzureSearch:Endpoint"]
            ?? throw new InvalidOperationException("Missing configuration: AzureSearch:Endpoint");
        _searchApiKey = configuration["AzureSearch:ApiKey"];

        var configuredBaseIndex = configuration["AzureSearch:Index"]
            ?? throw new InvalidOperationException("Missing configuration: AzureSearch:Index");

        var configuredPath = configuration["IndexVersioning:StateFile"] ?? "data/index-version-state.json";
        _stateFilePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);
        var stateDir = Path.GetDirectoryName(_stateFilePath) ?? environment.ContentRootPath;
        Directory.CreateDirectory(stateDir);

        _state = LoadState(configuredBaseIndex);
    }

    public string GetActiveIndex()
    {
        lock (_gate)
        {
            return _state.ActiveIndex;
        }
    }

    public IndexVersionState GetState()
    {
        lock (_gate)
        {
            return new IndexVersionState
            {
                ActiveIndex = _state.ActiveIndex,
                KnownIndexes = _state.KnownIndexes.ToList(),
                UpdatedAt = _state.UpdatedAt
            };
        }
    }

    public void SwitchActiveIndex(string indexVersion)
    {
        if (string.IsNullOrWhiteSpace(indexVersion))
            throw new ArgumentException("indexVersion is required", nameof(indexVersion));

        lock (_gate)
        {
            _state.ActiveIndex = indexVersion.Trim();
            if (!_state.KnownIndexes.Any(i => string.Equals(i, _state.ActiveIndex, StringComparison.OrdinalIgnoreCase)))
                _state.KnownIndexes.Add(_state.ActiveIndex);
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            PersistState();
        }

        _logger.LogInformation("Active index switched to {IndexVersion}", indexVersion);
    }

    public SearchClient CreateSearchClientForActiveIndex() => CreateSearchClient(GetActiveIndex());

    public SearchClient CreateSearchClient(string indexName)
    {
        var endpoint = new Uri(_searchEndpoint);
        if (string.IsNullOrWhiteSpace(_searchApiKey))
            return new SearchClient(endpoint, indexName, new DefaultAzureCredential());

        return new SearchClient(endpoint, indexName, new AzureKeyCredential(_searchApiKey));
    }

    private IndexVersionState LoadState(string configuredBaseIndex)
    {
        if (!File.Exists(_stateFilePath))
        {
            var initial = new IndexVersionState
            {
                ActiveIndex = configuredBaseIndex,
                KnownIndexes = new List<string> { configuredBaseIndex },
                UpdatedAt = DateTimeOffset.UtcNow
            };
            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(initial, new JsonSerializerOptions { WriteIndented = true }));
            return initial;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<IndexVersionState>(File.ReadAllText(_stateFilePath));
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.ActiveIndex))
                throw new InvalidOperationException("Invalid index version state file.");

            if (!parsed.KnownIndexes.Any(i => string.Equals(i, parsed.ActiveIndex, StringComparison.OrdinalIgnoreCase)))
                parsed.KnownIndexes.Add(parsed.ActiveIndex);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse index version state; rebuilding from configured base index.");
            return new IndexVersionState
            {
                ActiveIndex = configuredBaseIndex,
                KnownIndexes = new List<string> { configuredBaseIndex },
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private void PersistState()
    {
        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
    }
}
