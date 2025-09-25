using Microsoft.AspNetCore.Mvc;
using ChatAgent.Domain.Interfaces;

namespace ChatAgent.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TestController : ControllerBase
{
    private readonly IOrchestrator _orchestrator;
    private readonly ILogger<TestController> _logger;

    public TestController(IOrchestrator orchestrator, ILogger<TestController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Simple test endpoint - no CORS issues
    /// Access via: http://localhost:5000/api/test?message=Hello
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> TestMessage([FromQuery] string message = "Hello")
    {
        try
        {
            var sessionId = $"test-{DateTime.Now.Ticks}";
            _logger.LogInformation("Test endpoint called with message: {Message}, session: {Session}", message, sessionId);

            var response = await _orchestrator.ProcessMessageAsync(message, sessionId);

            return Ok(new
            {
                success = true,
                sessionId,
                userMessage = message,
                response = response.Content,
                agentId = response.AgentId,
                timestamp = response.Timestamp
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test endpoint failed");
            return Ok(new
            {
                success = false,
                error = ex.Message,
                stackTrace = ex.StackTrace
            });
        }
    }
}