using System.ComponentModel;
using ChatAgent.Application.Tools;
using ChatAgent.Application.Tools.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ChatAgent.Application.Plugins.Azure;

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
        [Description("Name of the connector to find (e.g., 'AWS', 'Amazon Web Services', 'Office 365', 'Azure Activity')")] string connectorName,
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

    [KernelFunction("InstallConnectorSolution")]
    [Description("Installs a connector solution from Microsoft Sentinel Content Hub into the specified workspace.")]
    public async Task<InstallConnectorSolution.Output> InstallConnectorSolutionAsync(
        [Description("Solution ID from Content Hub (e.g., 'azuresentinel.azure-sentinel-solution-amazonwebservices')")] string solutionId,
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Azure resource group name")] string resourceGroupName,
        [Description("Microsoft Sentinel workspace name")] string workspaceName,
        [Description("Version to install (default: 'latest')")] string version = "latest",
        [Description("Enable data connectors after installation (default: true)")] bool enableDataConnectors = true,
        CancellationToken cancellationToken = default)
    {
        var input = new InstallConnectorSolution.Input(
            SolutionId: solutionId,
            SubscriptionId: subscriptionId,
            ResourceGroupName: resourceGroupName,
            WorkspaceName: workspaceName,
            Version: version,
            EnableDataConnectors: enableDataConnectors,
            Parameters: null
        );

        _logger.LogInformation("Installing solution: {SolutionId} in workspace: {WorkspaceName}",
            solutionId, workspaceName);
        return await _toolHandlers.InstallConnectorSolutionHandler.HandleAsync(input, cancellationToken);
    }
}
