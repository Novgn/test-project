namespace ChatAgent.Domain.Entities;

public class ChatMessage
{
    public Guid Id { get; private set; }
    public string Content { get; private set; }
    public string Role { get; private set; }
    public DateTime Timestamp { get; private set; }
    public string? AgentId { get; private set; }
    public Dictionary<string, object> Metadata { get; private set; }

    public ChatMessage(string content, string role, string? agentId = null)
    {
        Id = Guid.NewGuid();
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Role = role ?? throw new ArgumentNullException(nameof(role));
        AgentId = agentId;
        Timestamp = DateTime.UtcNow;
        Metadata = new Dictionary<string, object>();
    }

    public void AddMetadata(string key, object value)
    {
        Metadata[key] = value;
    }
}