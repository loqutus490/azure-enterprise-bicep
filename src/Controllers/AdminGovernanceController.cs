using LegalRagApp.Models;
using LegalRagApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalRagApp.Controllers;

[ApiController]
[Route("admin")]
[Authorize(Policy = "ApiAccessPolicy")]
public sealed class AdminGovernanceController : ControllerBase
{
    private readonly IIndexVersionService _indexVersionService;
    private readonly ILineageService _lineageService;
    private readonly IReindexService _reindexService;

    public AdminGovernanceController(
        IIndexVersionService indexVersionService,
        ILineageService lineageService,
        IReindexService reindexService)
    {
        _indexVersionService = indexVersionService;
        _lineageService = lineageService;
        _reindexService = reindexService;
    }

    [HttpPost("index/switch")]
    public IActionResult SwitchIndex([FromBody] SwitchIndexRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IndexVersion))
            return BadRequest("indexVersion is required.");

        _indexVersionService.SwitchActiveIndex(request.IndexVersion);
        return Ok(_indexVersionService.GetState());
    }

    [HttpGet("documents/lineage")]
    public async Task<IActionResult> GetLineage(CancellationToken cancellationToken)
    {
        var lineage = await _lineageService.GetLineageAsync(cancellationToken);
        return Ok(lineage);
    }

    [HttpDelete("documents/{documentId}")]
    public async Task<IActionResult> DeleteDocument(string documentId, CancellationToken cancellationToken)
    {
        var deletedChunks = await _reindexService.DeleteDocumentAsync(documentId, cancellationToken);
        return Ok(new
        {
            documentId,
            deletedChunks,
            indexVersion = _indexVersionService.GetActiveIndex()
        });
    }
}
