namespace ChatAgent.Domain.Interfaces;

public interface IMcpToolProvider
{
    string Name { get; }
    string Description { get; }
    Task<object> ExecuteAsync(string toolName, Dictionary<string, object> parameters, CancellationToken cancellationToken = default);
    Task<List<ToolDescriptor>> GetAvailableToolsAsync(CancellationToken cancellationToken = default);
}

public class ToolDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, ParameterDescriptor> Parameters { get; set; } = new();
}

public class ParameterDescriptor
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public object? DefaultValue { get; set; }
}