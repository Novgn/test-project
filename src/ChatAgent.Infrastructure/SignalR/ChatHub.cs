using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;

namespace ChatAgent.Infrastructure.SignalR;

/// <summary>
/// SignalR Hub for real-time chat communication
///
/// Client Events (sent to frontend):
/// - Connected: Initial connection confirmation with sessionId
/// - ReceiveMessage: Chat message from agents/orchestrator
/// - Processing: Indicates message is being processed
/// - Error: Error messages
/// - SessionUpdated: Session ID was updated
/// - ConversationHistory: Full conversation history
/// - AvailableAgents: List of available agents/specialists
/// - JoinedGroup/LeftGroup: Group membership confirmations
///
/// Server Methods (callable from frontend):
/// - SetSessionId(sessionId): Set/update session ID
/// - SendMessage(message): Send chat message
/// - GetConversationHistory(): Get full history
/// - GetAvailableAgents(): Get list of agents
/// - JoinGroup(groupName): Join a SignalR group
/// - LeaveGroup(groupName): Leave a SignalR group
/// </summary>
public class ChatHub(IOrchestrator orchestrator, ILogger<ChatHub> logger) : Hub
{
    private readonly IOrchestrator _orchestrator = orchestrator;
    private readonly ILogger<ChatHub> _logger = logger;
    private static readonly Dictionary<string, string> _connectionToSession = [];

    public override async Task OnConnectedAsync()
    {
        // Try to get sessionId from query string, otherwise generate new one
        var httpContext = Context.GetHttpContext();
        var sessionId = httpContext?.Request.Query["sessionId"].FirstOrDefault()
                       ?? Guid.NewGuid().ToString();

        _connectionToSession[Context.ConnectionId] = sessionId;

        await Clients.Caller.SendAsync("Connected", new
        {
            sessionId,
            connectionId = Context.ConnectionId,
            timestamp = DateTime.UtcNow
        });

        _logger.LogInformation("Client connected: {ConnectionId} with session {SessionId}",
            Context.ConnectionId, sessionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectionToSession.TryGetValue(Context.ConnectionId, out var sessionId))
        {
            _connectionToSession.Remove(Context.ConnectionId);

            var conversation = await _orchestrator.GetConversationAsync(sessionId);
            if (conversation != null)
            {
                conversation.End();
            }
        }

        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SetSessionId(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            await Clients.Caller.SendAsync("Error", "Session ID cannot be empty.");
            return;
        }

        // Update the connection-to-session mapping
        _connectionToSession[Context.ConnectionId] = sessionId;

        _logger.LogInformation("Client {ConnectionId} set session to {SessionId}",
            Context.ConnectionId, sessionId);

        // Verify the conversation exists or create it
        var conversation = await _orchestrator.GetConversationAsync(sessionId);
        if (conversation is null)
        {
            _logger.LogInformation("Creating conversation for session {SessionId}", sessionId);
            // The orchestrator will create it on first message
        }

        await Clients.Caller.SendAsync("SessionUpdated", new
        {
            sessionId,
            connectionId = Context.ConnectionId,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Process a message from the client through the orchestrator
    /// </summary>
    public async Task SendMessage(string message)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out var sessionId))
        {
            await Clients.Caller.SendAsync("Error", "Session not found. Please reconnect.");
            return;
        }

        try
        {
            // Log preview of message for debugging
            var messagePreview = message.Length <= 50 ? message : $"{message[..50]}...";
            _logger.LogDebug("Processing message from session {SessionId}: {Message}",
                sessionId, messagePreview);

            // Notify client that processing has started
            await Clients.Caller.SendAsync("Processing", new
            {
                status = "processing",
                timestamp = DateTime.UtcNow
            });

            // Process through orchestrator
            var response = await _orchestrator.ProcessMessageAsync(message, sessionId);

            // Send response back to client
            await Clients.Caller.SendAsync("ReceiveMessage", new
            {
                content = response.Content,
                role = response.Role,
                agentId = response.AgentId,
                timestamp = response.Timestamp,
                metadata = response.Metadata
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for session {SessionId}", sessionId);
            await Clients.Caller.SendAsync("Error", $"Failed to process message: {ex.Message}");
        }
    }

    public async Task GetConversationHistory()
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out var sessionId))
        {
            await Clients.Caller.SendAsync("Error", "Session not found. Please reconnect.");
            return;
        }

        try
        {
            var conversation = await _orchestrator.GetConversationAsync(sessionId);
            await Clients.Caller.SendAsync("ConversationHistory", new
            {
                sessionId,
                messages = conversation.Messages.Select(m => new
                {
                    content = m.Content,
                    role = m.Role,
                    timestamp = m.Timestamp,
                    agentId = m.AgentId,
                    metadata = m.Metadata
                }),
                status = conversation.Status.ToString(),
                startedAt = conversation.StartedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversation history for session {SessionId}", sessionId);
            await Clients.Caller.SendAsync("Error", $"Failed to retrieve conversation history: {ex.Message}");
        }
    }

    /// <summary>
    /// Get list of available agents/specialists
    /// </summary>
    public async Task GetAvailableAgents()
    {
        try
        {
            var agents = await _orchestrator.GetAvailableAgentsAsync();
            await Clients.Caller.SendAsync("AvailableAgents", agents.Select(a => new
            {
                id = a.Id,
                name = a.Name,
                description = a.Description,
                type = a.Type.ToString(),
                capabilities = a.Capabilities
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving available agents");
            await Clients.Caller.SendAsync("Error", $"Failed to retrieve agents: {ex.Message}");
        }
    }

    /// <summary>
    /// Join a SignalR group (used for Sentinel setup sessions)
    /// </summary>
    public async Task JoinGroup(string groupName)
    {
        if (string.IsNullOrEmpty(groupName))
        {
            await Clients.Caller.SendAsync("Error", "Group name cannot be empty.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} joined group {GroupName}",
            Context.ConnectionId, groupName);

        await Clients.Caller.SendAsync("JoinedGroup", new
        {
            groupName,
            connectionId = Context.ConnectionId,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Leave a SignalR group
    /// </summary>
    public async Task LeaveGroup(string groupName)
    {
        if (string.IsNullOrEmpty(groupName))
        {
            await Clients.Caller.SendAsync("Error", "Group name cannot be empty.");
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Client {ConnectionId} left group {GroupName}",
            Context.ConnectionId, groupName);

        await Clients.Caller.SendAsync("LeftGroup", new
        {
            groupName,
            connectionId = Context.ConnectionId,
            timestamp = DateTime.UtcNow
        });
    }
}