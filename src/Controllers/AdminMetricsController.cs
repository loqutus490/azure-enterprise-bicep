using LegalRagApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LegalRagApp.Controllers;

[ApiController]
[Route("admin")]
public sealed class AdminMetricsController : ControllerBase
{
    private readonly IMetricsService _metricsService;

    public AdminMetricsController(IMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    [HttpGet("metrics")]
    [Authorize(Policy = "ApiAccessPolicy")]
    public IActionResult GetMetrics()
    {
        var snapshot = _metricsService.GetSnapshot(DateTimeOffset.UtcNow);
        return Ok(snapshot);
    }
}
