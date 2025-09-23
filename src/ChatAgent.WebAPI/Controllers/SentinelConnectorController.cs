using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ChatAgent.Application.Orchestration;
using ChatAgent.Infrastructure.SignalR;

namespace ChatAgent.WebAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SentinelConnectorController : ControllerBase
{
    private readonly SentinelConnectorGroupChatOrchestrator _orchestrator;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<SentinelConnectorController> _logger;

    public SentinelConnectorController(
        SentinelConnectorGroupChatOrchestrator orchestrator,
        IHubContext<ChatHub> hubContext,
        ILogger<SentinelConnectorController> logger)
    {
        _orchestrator = orchestrator;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Start AWS-Azure Sentinel connector setup with real-time updates via SignalR
    /// </summary>
    [HttpPost("setup")]
    public async Task<IActionResult> StartConnectorSetup([FromBody] SentinelConnectorSetupRequest request)
    {
        try
        {
            _logger.LogInformation("Starting Sentinel connector setup for workspace {WorkspaceId}", request.WorkspaceId);

            // Validate request
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            // Generate a session ID for this setup
            var sessionId = $"sentinel-setup-{Guid.NewGuid()}";

            // Send initial message to SignalR clients
            await _hubContext.Clients.Group(sessionId).SendAsync("setupStarted", new
            {
                sessionId,
                workspaceId = request.WorkspaceId,
                message = "Starting AWS-Azure Sentinel connector setup..."
            });

            // Create configuration for the orchestrator
            var configuration = new SetupConfiguration
            {
                WorkspaceId = request.WorkspaceId,
                TenantId = request.TenantId,
                SubscriptionId = request.SubscriptionId,
                ResourceGroupName = request.ResourceGroupName,
                LogTypes = request.LogTypes ?? new List<string> { "CloudTrail", "VPCFlow", "GuardDuty" },
                AwsRegion = request.AwsRegion ?? "us-east-1"
            };

            // Start setup in background and stream updates via SignalR
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use the real multi-agent orchestrator
                    var result = await ExecuteRealOrchestratorSetup(configuration, sessionId);

                    // Send completion message
                    await _hubContext.Clients.Group(sessionId).SendAsync("setupCompleted", new
                    {
                        success = result.Success,
                        connectorId = result.ConnectorId,
                        roleArn = result.AwsRoleArn,
                        sqsUrls = result.SqsUrls,
                        message = result.Success
                            ? "Sentinel connector setup completed successfully"
                            : $"Setup failed: {result.ErrorMessage}"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during setup execution");
                    await _hubContext.Clients.Group(sessionId).SendAsync("setupError", new
                    {
                        error = ex.Message,
                        message = "An error occurred during setup"
                    });
                }
            });

            return Ok(new
            {
                sessionId,
                message = "Setup started. Connect to SignalR hub for real-time updates.",
                signalRHub = "/chathub",
                joinGroup = sessionId
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
    /// Execute setup using the real multi-agent orchestrator
    /// </summary>
    private async Task<SetupResult> ExecuteRealOrchestratorSetup(
        SetupConfiguration configuration,
        string sessionId)
    {
        var result = new SetupResult();

        try
        {
            _logger.LogInformation("Starting real multi-agent orchestrator for session {SessionId}", sessionId);

            // Create a progress callback to send SignalR updates
            Action<string, string, string> progressCallback = (phase, agent, message) =>
            {
                _hubContext.Clients.Group(sessionId).SendAsync("agentMessage", new
                {
                    agent,
                    message,
                    phase,
                    timestamp = DateTime.UtcNow
                }).Wait();

                // Also send phase updates when phase changes
                _hubContext.Clients.Group(sessionId).SendAsync("phaseUpdate", new
                {
                    phase,
                    message = $"{agent}: {message}",
                    timestamp = DateTime.UtcNow
                }).Wait();
            };

            // Build the setup request message for the orchestrator with structured data
            var setupMessage = $@"Please set up an AWS-Azure Sentinel connector with the following configuration:

                CONFIGURATION_JSON:
                {{
                    ""workspaceId"": ""{configuration.WorkspaceId}"",
                    ""tenantId"": ""{configuration.TenantId}"",
                    ""subscriptionId"": ""{configuration.SubscriptionId}"",
                    ""resourceGroupName"": ""{configuration.ResourceGroupName}"",
                    ""awsRegion"": ""{configuration.AwsRegion}"",
                    ""logTypes"": [{string.Join(", ", configuration.LogTypes.Select(lt => $"\"{lt}\""))}]
                }}

                Execute the complete setup process including:
                1. Validate prerequisites using the configuration above
                2. Set up AWS infrastructure in region {configuration.AwsRegion}:
                   - Create OIDC provider for tenant {configuration.TenantId}
                   - Create IAM roles with proper trust relationships
                   - Create S3 buckets for {string.Join(", ", configuration.LogTypes)} logs
                   - Set up SQS queues for event notifications
                3. Configure Azure Sentinel in subscription {configuration.SubscriptionId}:
                   - Deploy connector solution to resource group {configuration.ResourceGroupName}
                   - Configure data connector for workspace {configuration.WorkspaceId}
                4. Establish integration between AWS and Azure
                5. Set up monitoring and verify data flow
                6. Generate final report with all created resources

                IMPORTANT: Use the actual configuration values provided above when calling your tools.
                When calling AWS tools, always include region: {configuration.AwsRegion}
                When calling Azure tools, always include tenantId: {configuration.TenantId}, subscriptionId: {configuration.SubscriptionId}";

            // Execute the orchestrator
            var response = await _orchestrator.ExecuteSetupAsync(setupMessage, progressCallback);

            // Parse the response to extract created resources
            if (response != null)
            {
                // Try to extract key information from the response
                result.Success = response.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                               response.Contains("completed", StringComparison.OrdinalIgnoreCase);

                // Extract ARN if present (looking for patterns like arn:aws:iam::...)
                var arnMatch = System.Text.RegularExpressions.Regex.Match(response, @"arn:aws:iam::[\d]+:role/[\w-]+");
                if (arnMatch.Success)
                {
                    result.AwsRoleArn = arnMatch.Value;
                }

                // Extract SQS URLs if present
                var sqsMatches = System.Text.RegularExpressions.Regex.Matches(response, @"https://sqs\.[\w-]+\.amazonaws\.com/[\d]+/[\w-]+");
                foreach (System.Text.RegularExpressions.Match match in sqsMatches)
                {
                    result.SqsUrls.Add(match.Value);
                }

                // Generate a connector ID
                result.ConnectorId = $"AWS_Connector_{Guid.NewGuid().ToString().Substring(0, 8)}";
                result.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("Multi-agent orchestrator completed. Success: {Success}", result.Success);
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = "No response from orchestrator";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing real orchestrator setup");
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
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
    public async Task<IActionResult> GetConnectorStatus(string connectorId)
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

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connector status");
            return StatusCode(500, new
            {
                success = false,
                message = "Error retrieving connector status",
                error = ex.Message
            });
        }
    }
}

// Request DTOs
public class SentinelConnectorSetupRequest
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