using ChatAgent.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Text.Json;

namespace ChatAgent.Infrastructure.McpTools;

public class McpServerToolProvider : IMcpToolProvider, IAsyncDisposable
{
    private readonly ILogger<McpServerToolProvider> _logger;
    private IMcpClient? _mcpClient;
    private StdioClientTransport? _transport;
    private List<ToolDescriptor>? _cachedTools;
    private readonly string _command;
    private readonly string[] _arguments;

    public string Name { get; }
    public string Description { get; }

    public McpServerToolProvider(
        string name,
        string description,
        string command,
        string[] arguments,
        ILogger<McpServerToolProvider> logger)
    {
        Name = name;
        Description = description;
        _command = command;
        _arguments = arguments;
        _logger = logger;
    }

    public async Task<object> ExecuteAsync(string toolName, Dictionary<string, object> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            if (_mcpClient == null)
            {
                throw new InvalidOperationException("MCP client is not connected");
            }

            // Convert parameters to the format expected by the SDK
            var toolParams = new Dictionary<string, object?>();
            foreach (var param in parameters)
            {
                toolParams[param.Key] = param.Value;
            }

            var result = await _mcpClient.CallToolAsync(
                toolName,
                toolParams,
                cancellationToken: cancellationToken);

            // Extract the content from the result
            // ContentBlock might have different properties in this SDK version
            var firstContent = result.Content.FirstOrDefault();
            if (firstContent != null)
            {
                // Try to serialize the content to see what it contains
                var jsonContent = JsonSerializer.Serialize(firstContent);
                return jsonContent;
            }

            return new { success = true, content = result.Content };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing MCP tool {ToolName}", toolName);
            throw;
        }
    }

    public async Task<List<ToolDescriptor>> GetAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedTools != null)
        {
            return _cachedTools;
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken);

            if (_mcpClient == null)
            {
                throw new InvalidOperationException("MCP client is not connected");
            }

            // ListToolsAsync might have different parameters in this version
            var tools = await _mcpClient.ListToolsAsync();

            _cachedTools = tools.Select(tool => new ToolDescriptor
            {
                Name = tool.Name,
                Description = tool.Description ?? string.Empty,
                Parameters = ConvertToolParameters(tool)
            }).ToList();

            return _cachedTools;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available MCP tools");
            return new List<ToolDescriptor>();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_mcpClient == null || _transport == null)
        {
            try
            {
                _logger.LogInformation("Connecting to MCP server: {Name} using command: {Command} {Arguments}",
                    Name, _command, string.Join(" ", _arguments));

                // Create transport with stdio - simpler configuration
                _transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = Name,
                    Command = _command,
                    Arguments = _arguments
                });

                // Create the client using static factory method
                // The factory method signature is CreateAsync(transport, options, loggerFactory)
                _mcpClient = await McpClientFactory.CreateAsync(_transport, new McpClientOptions
                {
                    // ClientInfo might be structured differently
                });

                _logger.LogInformation("Successfully connected to MCP server: {Name}", Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to MCP server: {Name}", Name);
                throw;
            }
        }
    }

    private Dictionary<string, ParameterDescriptor> ConvertToolParameters(McpClientTool tool)
    {
        var parameters = new Dictionary<string, ParameterDescriptor>();

        // The MCP SDK may provide parameter information through the tool's schema
        // For now, we'll create basic parameter descriptors
        // This would need to be enhanced based on the actual schema structure

        if (tool.Name != null)
        {
            // Add a generic parameter structure for now
            // In a real implementation, you would parse the tool's input schema
            parameters["input"] = new ParameterDescriptor
            {
                Type = "object",
                Description = "Tool input parameters",
                Required = false,
                DefaultValue = null
            };
        }

        return parameters;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_mcpClient != null)
            {
                // Dispose the client if it implements IDisposable or IAsyncDisposable
                if (_mcpClient is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (_mcpClient is IDisposable disposableClient)
                {
                    disposableClient.Dispose();
                }
                _mcpClient = null;
            }

            if (_transport != null)
            {
                // StdioClientTransport is a sealed class - just set to null
                // The transport will be cleaned up by the garbage collector
                _transport = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing MCP client");
        }
    }
}