using System.ComponentModel;
using ChatAgent.Application.Plugins.Azure;
using ChatAgent.Application.Tools;
using ChatAgent.Application.Tools.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ChatAgent.Application.Plugins;

public class AzurePlugin
{
    private readonly ILogger<AzurePlugin> _logger;
    private readonly AzureToolHandlers _toolHandlers;

    public AzurePlugin(ILogger<AzurePlugin> logger, AzureToolHandlers toolHandlers)
    {
        _logger = logger;
        _toolHandlers = toolHandlers;
    }

    [KernelFunction("FindConnectorSolution")]
    [Description("Finds a suitable Azure Connector solution based on the provided requirements.")]
    public async Task<FindConnectorSolution.Output> FindConnectorSolutionAsync(
        [Description("Name of the connector to find (e.g., 'AWS', 'Amazon Web Services')")] string connectorName,
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Azure resource group name")] string resourceGroupName,
        [Description("Microsoft Sentinel workspace name")] string workspaceName,
        CancellationToken cancellationToken = default)
    {
        var input = new FindConnectorSolution.Input(
            ConnectorName: connectorName,
            SubscriptionId: subscriptionId,
            ResourceGroupName: resourceGroupName,
            WorkspaceName: workspaceName
        );

        _logger.LogInformation("Finding connector solution for: {ConnectorName} in workspace: {WorkspaceName}",
            connectorName, workspaceName);
        return await _toolHandlers.FindConnectorSolutionHandler.HandleAsync(input, cancellationToken);
    }
}
