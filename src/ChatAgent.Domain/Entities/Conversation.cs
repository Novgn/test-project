namespace ChatAgent.Domain.Entities;

public class Conversation
{
    public Guid Id { get; private set; }
    public string SessionId { get; private set; }
    public List<ChatMessage> Messages { get; private set; }
    public ConversationStatus Status { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public Dictionary<string, object> Context { get; private set; }

    public Conversation(string sessionId)
    {
        Id = Guid.NewGuid();
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        Messages = new List<ChatMessage>();
        Status = ConversationStatus.Active;
        StartedAt = DateTime.UtcNow;
        Context = new Dictionary<string, object>();
    }

    public void AddMessage(ChatMessage message)
    {
        Messages.Add(message);
    }

    public void UpdateContext(string key, object value)
    {
        Context[key] = value;
    }

    public void End()
    {
        Status = ConversationStatus.Completed;
        EndedAt = DateTime.UtcNow;
    }
}

public enum ConversationStatus
{
    Active,
    Paused,
    Completed,
    Failed
}