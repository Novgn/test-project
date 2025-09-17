using ChatAgent.Domain.Entities;

namespace ChatAgent.Domain.Interfaces;

public interface IOrchestrator
{
    Task<ChatMessage> ProcessMessageAsync(string message, string sessionId, CancellationToken cancellationToken = default);
    Task<Conversation> GetConversationAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<List<Agent>> GetAvailableAgentsAsync(CancellationToken cancellationToken = default);
    Task RegisterAgentAsync(Agent agent, CancellationToken cancellationToken = default);
}