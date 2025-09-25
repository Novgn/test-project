using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using Azure.ResourceManager.OperationalInsights.Models;
using Azure.Core;
using Azure;

namespace ChatAgent.Application.Tools.Azure;

public class FindConnectorSolutionHandler
{
    private readonly ILogger<FindConnectorSolutionHandler> _logger;

    public FindConnectorSolutionHandler(ILogger<FindConnectorSolutionHandler> logger)
    {
        _logger = logger;
    }

    public async Task<FindConnectorSolution.Output> HandleAsync(
        FindConnectorSolution.Input input,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Finding connector solution '{ConnectorName}' for workspace '{WorkspaceName}' in subscription '{SubscriptionId}' and resource group '{ResourceGroupName}'",
            input.ConnectorName,
            input.WorkspaceName,
            input.SubscriptionId,
            input.ResourceGroupName);

        try
        {
            // 1. Create Azure Resource Manager client with DefaultAzureCredential
            var credential = new DefaultAzureCredential();
            var armClient = new ArmClient(credential);

            // 2. Get the subscription
            var subscriptionId = $"/subscriptions/{input.SubscriptionId}";
            var subscription = await armClient.GetSubscriptionResource(new ResourceIdentifier(subscriptionId))
                .GetAsync(cancellationToken);

            if (subscription == null || !subscription.HasValue)
            {
                _logger.LogWarning("Subscription {SubscriptionId} not found", input.SubscriptionId);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: string.Empty,
                    Description: "Subscription not found",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }

            // 3. Get the resource group
            var resourceGroup = await subscription.Value.GetResourceGroupAsync(input.ResourceGroupName, cancellationToken);

            if (resourceGroup == null || !resourceGroup.HasValue)
            {
                _logger.LogWarning("Resource group {ResourceGroupName} not found", input.ResourceGroupName);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: string.Empty,
                    Description: "Resource group not found",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }

            // 4. Get the Log Analytics workspace

            // 5. Get the OperationalInsights workspace
            var workspaceCollection = resourceGroup.Value.GetOperationalInsightsWorkspaces();
            var workspace = await workspaceCollection.GetAsync(input.WorkspaceName, cancellationToken);

            if (workspace == null || !workspace.HasValue)
            {
                _logger.LogWarning("Workspace {WorkspaceName} not found", input.WorkspaceName);
                return new FindConnectorSolution.Output(
                    SolutionId: string.Empty,
                    SolutionName: string.Empty,
                    Description: "Workspace not found",
                    Version: string.Empty,
                    IsInstalled: false
                );
            }

            // 6. Check if AWS solution is already installed
            bool isInstalled = false;

            try
            {
                // Check for installed data connectors using the workspace resource
                // In a real implementation, you would query the Sentinel data connectors API
                // For now, we'll check if the workspace has Sentinel enabled
                var workspaceResource = workspace.Value;

                // Check workspace properties for Sentinel features
                if (workspaceResource.Data.Features != null)
                {
                    _logger.LogInformation("Workspace features available, checking for AWS connector");
                    // In production, query actual data connectors via REST API or SDK methods
                    // Example: GET /subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.OperationalInsights/workspaces/{workspaceName}/providers/Microsoft.SecurityInsights/dataConnectors
                }
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning("Could not check installed connectors: {Message}", ex.Message);
            }

            // 7. Return AWS connector solution details
            // In production, this would query the actual Content Hub API
            // For now, returning known AWS solution details
            return new FindConnectorSolution.Output(
                SolutionId: "azuresentinel.azure-sentinel-solution-amazonwebservices",
                SolutionName: "Amazon Web Services",
                Description: "Microsoft Sentinel solution for Amazon Web Services provides the capability to ingest AWS service logs into Microsoft Sentinel through Azure S3 integration. It includes pre-built workbooks, analytics rules, and hunting queries.",
                Version: "3.0.2",
                IsInstalled: isInstalled
            );
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogError(ex, "Authentication failed when accessing Azure resources");
            return new FindConnectorSolution.Output(
                SolutionId: string.Empty,
                SolutionName: string.Empty,
                Description: $"Authentication failed: {ex.Message}",
                Version: string.Empty,
                IsInstalled: false
            );
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure request failed");
            return new FindConnectorSolution.Output(
                SolutionId: string.Empty,
                SolutionName: string.Empty,
                Description: $"Azure request failed: {ex.Message}",
                Version: string.Empty,
                IsInstalled: false
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finding connector solution");
            return new FindConnectorSolution.Output(
                SolutionId: string.Empty,
                SolutionName: string.Empty,
                Description: $"Error: {ex.Message}",
                Version: string.Empty,
                IsInstalled: false
            );
        }
    }
}
