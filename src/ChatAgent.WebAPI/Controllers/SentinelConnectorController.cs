using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ChatAgent.Application.Orchestration;
using ChatAgent.Infrastructure.SignalR;
using ChatAgent.Domain.Interfaces;

namespace ChatAgent.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SentinelConnectorController : ControllerBase
{
    private readonly IOrchestrator _orchestrator;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<SentinelConnectorController> _logger;

    public SentinelConnectorController(
        IOrchestrator orchestrator,
        IHubContext<ChatHub> hubContext,
        ILogger<SentinelConnectorController> logger)
    {
        _orchestrator = orchestrator;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Send a conversational message to the assistant
    /// </summary>
    [HttpPost("chat/{sessionId}")]
    public async Task<IActionResult> Chat(string sessionId, [FromBody] ChatRequest request)
    {
        try
        {
            _logger.LogInformation("User message for session {SessionId}: {Message}", sessionId, request.Message);

            // Process the message conversationally
            var response = await _orchestrator.ProcessMessageAsync(request.Message, sessionId);

            // Send real-time update via SignalR
            await _hubContext.Clients.Group(sessionId).SendAsync("assistantResponse", new
            {
                speaker = response.AgentId ?? "assistant",
                message = response.Content,
                timestamp = response.Timestamp,
                isAgent = response.AgentId != "assistant" && response.AgentId != "system"
            });

            return Ok(new
            {
                success = true,
                message = response.Content,
                speaker = response.AgentId ?? "assistant",
                timestamp = response.Timestamp
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Sentinel connector setup");
            return StatusCode(500, new
            {
                success = false,
                message = "An error occurred while starting setup",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Start a new session for user-driven orchestration
    /// </summary>
    [HttpPost("session/start")]
    public async Task<IActionResult> StartSession([FromBody] SessionStartRequest request)
    {
        try
        {
            var sessionId = $"sentinel-{Guid.NewGuid().ToString().Substring(0, 8)}";

            // Store configuration for this session (could be in memory cache or database)
            // For now, we'll include it in the response for the client to track

            await _hubContext.Clients.Group(sessionId).SendAsync("sessionStarted", new
            {
                sessionId,
                configuration = request,
                welcomeMessage = @"Hello! I'm here to help you set up your AWS-Azure Sentinel connector. 🚀

I'll guide you through the entire process step by step. We'll:
1. Validate your prerequisites
2. Set up AWS resources
3. Configure Azure Sentinel
4. Connect everything together
5. Verify it's all working

You can talk to me naturally - just tell me what you'd like to do, ask questions, or say 'help' if you need guidance.

Ready to begin? Just say 'let's start' or tell me what you'd like to do first!"
            });

            return Ok(new
            {
                sessionId,
                configuration = request,
                specialists = new[]
                {
                    new { name = "Coordinator", role = "Guides you through the process" },
                    new { name = "AWS Expert", role = "Handles AWS infrastructure" },
                    new { name = "Azure Specialist", role = "Configures Sentinel" },
                    new { name = "Integration Expert", role = "Connects AWS and Azure" },
                    new { name = "Monitor", role = "Validates and tests" }
                },
                tip = "Just chat naturally! For example: 'I need help setting up Sentinel to collect AWS CloudTrail logs'"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting session");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get available specialists and their roles
    /// </summary>
    [HttpGet("specialists")]
    public async Task<IActionResult> GetSpecialists()
    {
        try
        {
            var agents = await _orchestrator.GetAvailableAgentsAsync();
            var specialists = agents.Select(a => new
            {
                name = a.Name,
                role = a.Description,
                capabilities = a.Capabilities,
                available = true
            });
            return Ok(specialists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agents");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get conversation history for a session
    /// </summary>
    [HttpGet("session/{sessionId}/history")]
    public async Task<IActionResult> GetSessionHistory(string sessionId)
    {
        try
        {
            var conversation = await _orchestrator.GetConversationAsync(sessionId);
            return Ok(new
            {
                sessionId,
                messages = conversation.Messages.Select(m => new
                {
                    speaker = m.Role == "user" ? "You" : (m.AgentId ?? "Assistant"),
                    message = m.Content,
                    timestamp = m.Timestamp,
                    isUser = m.Role == "user"
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting session history");
            return StatusCode(500, new { error = ex.Message });
        }
    }


    private async Task SendPhaseUpdate(string sessionId, string phase, string message)
    {
        await _hubContext.Clients.Group(sessionId).SendAsync("phaseUpdate", new
        {
            phase,
            message,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Join a SignalR group to receive real-time updates for a setup session
    /// </summary>
    [HttpPost("join-session/{sessionId}")]
    public IActionResult JoinSession(string sessionId, [FromQuery] string connectionId)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} joining session {SessionId}", connectionId, sessionId);

            return Ok(new
            {
                sessionId,
                message = "Joined session. You will receive real-time updates."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining session");
            return StatusCode(500, new
            {
                success = false,
                message = "Error joining session",
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Get the status of an existing Sentinel connector
    /// </summary>
    [HttpGet("status/{connectorId}")]
    public Task<IActionResult> GetConnectorStatus(string connectorId)
    {
        try
        {
            _logger.LogInformation("Getting status for connector {ConnectorId}", connectorId);

            // Query real status from the orchestrator or Azure API
            // For now, return a structure that would be filled with real data
            var status = new
            {
                connectorId,
                status = "Pending",
                message = "Real-time status would be queried from Azure Sentinel API",
                note = "Requires Azure credentials and connector ID from actual deployment"
            };

            return Task.FromResult<IActionResult>(Ok(status));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connector status");
            return Task.FromResult<IActionResult>(StatusCode(500, new
            {
                success = false,
                message = "Error retrieving connector status",
                error = ex.Message
            }));
        }
    }
}

// Request DTOs
public class ChatRequest
{
    public required string Message { get; set; }
}

public class SessionStartRequest
{
    public required string WorkspaceId { get; set; }
    public required string TenantId { get; set; }
    public required string SubscriptionId { get; set; }
    public required string ResourceGroupName { get; set; }
    public List<string>? LogTypes { get; set; }
    public string? AwsRegion { get; set; }
}

// Configuration and Result DTOs
public class SetupConfiguration
{
    public required string WorkspaceId { get; set; }
    public required string TenantId { get; set; }
    public required string SubscriptionId { get; set; }
    public required string ResourceGroupName { get; set; }
    public List<string> LogTypes { get; set; } = new();
    public string AwsRegion { get; set; } = "us-east-1";
}

public class SetupResult
{
    public bool Success { get; set; }
    public string? ConnectorId { get; set; }
    public string? AwsRoleArn { get; set; }
    public List<string> SqsUrls { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime? CompletedAt { get; set; }
}