using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;

namespace ChatAgent.Infrastructure.SignalR;

public class ChatHub : Hub
{
    private readonly IOrchestrator _orchestrator;
    private readonly ILogger<ChatHub> _logger;
    private static readonly Dictionary<string, string> _connectionToSession = new();

    public ChatHub(IOrchestrator orchestrator, ILogger<ChatHub> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

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
        if (conversation == null)
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

    public async Task SendMessage(string message)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out var sessionId))
        {
            await Clients.Caller.SendAsync("Error", "Session not found. Please reconnect.");
            return;
        }

        try
        {
            _logger.LogDebug("Processing message from session {SessionId}: {Message}",
                sessionId, message.Substring(0, Math.Min(50, message.Length)));

            await Clients.Caller.SendAsync("Processing", new
            {
                status = "processing",
                timestamp = DateTime.UtcNow
            });

            var response = await _orchestrator.ProcessMessageAsync(message, sessionId);

            await Clients.Caller.SendAsync("ReceiveMessage", new
            {
                content = response.Content,  // Changed from 'message' to 'content'
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
}