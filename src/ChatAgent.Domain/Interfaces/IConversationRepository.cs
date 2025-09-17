using ChatAgent.Domain.Entities;

namespace ChatAgent.Domain.Interfaces;

public interface IConversationRepository
{
    Task<Conversation?> GetBySessionIdAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<Conversation> CreateAsync(Conversation conversation, CancellationToken cancellationToken = default);
    Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default);
    Task<List<Conversation>> GetActiveConversationsAsync(CancellationToken cancellationToken = default);
}