using LegalRagApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LegalRagApp.Controllers;

[ApiController]
[Route("")]
public sealed class HealthController : ControllerBase
{
    private readonly IHealthCheckService _healthCheckService;

    public HealthController(IHealthCheckService healthCheckService)
    {
        _healthCheckService = healthCheckService;
    }

    [HttpGet("health")]
    public async Task<IActionResult> GetHealth(CancellationToken cancellationToken)
    {
        var status = await _healthCheckService.GetHealthAsync(cancellationToken);
        return Ok(status);
    }
}
