using ChatAgent.Domain.Entities;
using ChatAgent.Domain.Interfaces;
using System.Collections.Concurrent;

namespace ChatAgent.Infrastructure.Repositories;

public class InMemoryConversationRepository : IConversationRepository
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();

    public Task<Conversation?> GetBySessionIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _conversations.TryGetValue(sessionId, out var conversation);
        return Task.FromResult(conversation);
    }

    public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        if (!_conversations.TryAdd(conversation.SessionId, conversation))
        {
            throw new InvalidOperationException($"Conversation with session ID {conversation.SessionId} already exists");
        }
        return Task.FromResult(conversation);
    }

    public Task UpdateAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        _conversations[conversation.SessionId] = conversation;
        return Task.CompletedTask;
    }

    public Task<List<Conversation>> GetActiveConversationsAsync(CancellationToken cancellationToken = default)
    {
        var activeConversations = _conversations.Values
            .Where(c => c.Status == ConversationStatus.Active)
            .ToList();
        return Task.FromResult(activeConversations);
    }
}