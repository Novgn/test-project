namespace ChatAgent.Domain.Entities;

public class Agent
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public AgentType Type { get; private set; }
    public Dictionary<string, string> Configuration { get; private set; }
    public List<string> Capabilities { get; private set; }

    public Agent(string id, string name, string description, AgentType type)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Type = type;
        Configuration = new Dictionary<string, string>();
        Capabilities = new List<string>();
    }

    public void AddCapability(string capability)
    {
        if (!Capabilities.Contains(capability))
            Capabilities.Add(capability);
    }

    public void UpdateConfiguration(string key, string value)
    {
        Configuration[key] = value;
    }
}

public enum AgentType
{
    Primary,
    Specialist,
    Tool,
    Coordinator
}