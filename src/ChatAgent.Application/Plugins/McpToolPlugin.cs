using Microsoft.SemanticKernel;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;
using System.ComponentModel;

namespace ChatAgent.Application.Plugins;

public class McpToolPlugin
{
    private readonly List<IMcpToolProvider> _toolProviders;
    private readonly ILogger<McpToolPlugin> _logger;

    public McpToolPlugin(IEnumerable<IMcpToolProvider> toolProviders, ILogger<McpToolPlugin> logger)
    {
        _toolProviders = toolProviders.ToList();
        _logger = logger;
    }

    [KernelFunction, Description("Execute an MCP tool with the given parameters")]
    public async Task<string> ExecuteToolAsync(
        [Description("The name of the MCP tool provider")] string providerName,
        [Description("The specific tool to execute")] string toolName,
        [Description("Parameters for the tool as key-value pairs")] Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default)
    {
        var provider = _toolProviders.FirstOrDefault(p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            _logger.LogWarning("MCP tool provider {Provider} not found", providerName);
            return $"Tool provider '{providerName}' not found";
        }

        try
        {
            var result = await provider.ExecuteAsync(toolName, parameters, cancellationToken);
            return System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing MCP tool {Tool} from provider {Provider}", toolName, providerName);
            return $"Error executing tool: {ex.Message}";
        }
    }

    [KernelFunction, Description("List all available MCP tools")]
    public async Task<string> ListAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        var allTools = new List<object>();

        foreach (var provider in _toolProviders)
        {
            try
            {
                var tools = await provider.GetAvailableToolsAsync(cancellationToken);
                allTools.Add(new
                {
                    Provider = provider.Name,
                    Description = provider.Description,
                    Tools = tools
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tools from provider {Provider}", provider.Name);
            }
        }

        return System.Text.Json.JsonSerializer.Serialize(allTools, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [KernelFunction, Description("Get detailed information about a specific MCP tool")]
    public async Task<string> GetToolInfoAsync(
        [Description("The name of the MCP tool provider")] string providerName,
        [Description("The specific tool name")] string toolName,
        CancellationToken cancellationToken = default)
    {
        var provider = _toolProviders.FirstOrDefault(p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            return $"Tool provider '{providerName}' not found";
        }

        try
        {
            var tools = await provider.GetAvailableToolsAsync(cancellationToken);
            var tool = tools.FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));

            if (tool == null)
            {
                return $"Tool '{toolName}' not found in provider '{providerName}'";
            }

            return System.Text.Json.JsonSerializer.Serialize(tool, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tool info for {Tool} from provider {Provider}", toolName, providerName);
            return $"Error getting tool info: {ex.Message}";
        }
    }
}