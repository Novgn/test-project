using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using Azure.Core;
using Azure;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatAgent.Application.Tools.Azure;

public class InstallConnectorSolutionHandler(ILogger<InstallConnectorSolutionHandler> logger)
{
    private readonly ILogger<InstallConnectorSolutionHandler> _logger = logger;
    private readonly HttpClient _httpClient = new();

    public async Task<InstallConnectorSolution.Output> HandleAsync(
        InstallConnectorSolution.Input input,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Checking solution '{SolutionId}' in workspace '{WorkspaceName}'",
            input.SolutionId,
            input.WorkspaceName);

        var installedComponents = new List<string>();
        var enabledDataConnectors = new List<string>();
        var configurationRequired = new Dictionary<string, string>();

        try
        {
            // 1. Create Azure Resource Manager client
            var credential = new DefaultAzureCredential();
            var armClient = new ArmClient(credential);

            // 2. Validate the workspace exists
            var subscriptionId = $"/subscriptions/{input.SubscriptionId}";
            var subscription = await armClient.GetSubscriptionResource(new ResourceIdentifier(subscriptionId))
                .GetAsync(cancellationToken);

            if (subscription == null || !subscription.HasValue)
            {
                return new InstallConnectorSolution.Output(
                    Success: false,
                    OperationId: string.Empty,
                    Status: "Failed",
                    Message: "Subscription not found",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: null
                );
            }

            var resourceGroup = await subscription.Value.GetResourceGroupAsync(input.ResourceGroupName, cancellationToken);
            if (resourceGroup == null || !resourceGroup.HasValue)
            {
                return new InstallConnectorSolution.Output(
                    Success: false,
                    OperationId: string.Empty,
                    Status: "Failed",
                    Message: "Resource group not found",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: null
                );
            }

            var workspaceCollection = resourceGroup.Value.GetOperationalInsightsWorkspaces();
            var workspace = await workspaceCollection.GetAsync(input.WorkspaceName, cancellationToken);
            if (workspace == null || !workspace.HasValue)
            {
                return new InstallConnectorSolution.Output(
                    Success: false,
                    OperationId: string.Empty,
                    Status: "Failed",
                    Message: "Workspace not found",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: null
                );
            }

            // 3. Get access token
            var scopes = new[] { "https://management.azure.com/.default" };
            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext(scopes),
                cancellationToken);

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            // 4. Check if solution is already installed
            var checkUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                $"/resourceGroups/{input.ResourceGroupName}" +
                $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                $"/providers/Microsoft.SecurityInsights/contentProductPackages?api-version=2024-09-01";

            var checkResponse = await _httpClient.GetAsync(checkUrl, cancellationToken);
            if (checkResponse.IsSuccessStatusCode)
            {
                var content = await checkResponse.Content.ReadAsStringAsync(cancellationToken);
                var data = JsonDocument.Parse(content);

                if (data.RootElement.TryGetProperty("value", out var packages))
                {
                    foreach (var package in packages.EnumerateArray())
                    {
                        if (package.TryGetProperty("properties", out var props))
                        {
                            string? contentId = null;
                            if (props.TryGetProperty("contentId", out var id))
                            {
                                contentId = id.GetString();
                            }

                            if (contentId == input.SolutionId)
                            {
                                var isInstalled = false;
                                if (props.TryGetProperty("isInstalled", out var installed))
                                {
                                    isInstalled = installed.GetBoolean();
                                }

                                if (isInstalled)
                                {
                                    _logger.LogInformation("Solution {SolutionId} is already installed", input.SolutionId);

                                    // Get installed components
                                    if (props.TryGetProperty("installedVersion", out var version))
                                    {
                                        installedComponents.Add($"Version: {version.GetString()}");
                                    }

                                    if (props.TryGetProperty("contentItems", out var items))
                                    {
                                        foreach (var item in items.EnumerateArray())
                                        {
                                            if (item.TryGetProperty("contentKind", out var kind) &&
                                                item.TryGetProperty("displayName", out var name))
                                            {
                                                var component = $"{kind.GetString()}: {name.GetString()}";
                                                installedComponents.Add(component);
                                            }
                                        }
                                    }

                                    return new InstallConnectorSolution.Output(
                                        Success: true,
                                        OperationId: string.Empty,
                                        Status: "AlreadyInstalled",
                                        Message: $"Solution {input.SolutionId} is already installed in the workspace",
                                        InstalledComponents: installedComponents,
                                        EnabledDataConnectors: enabledDataConnectors,
                                        ConfigurationRequired: configurationRequired
                                    );
                                }
                            }
                        }
                    }
                }
            }

            // 5. Provide installation instructions
            // Note: Automated installation of Content Hub solutions requires complex ARM template orchestration
            // For now, we'll provide manual instructions

            var portalUrl = $"https://portal.azure.com/#blade/Microsoft_Azure_Security_Insights/ContentHubBlade" +
                $"/subscriptionId/{input.SubscriptionId}" +
                $"/resourceGroup/{input.ResourceGroupName}" +
                $"/workspaceName/{input.WorkspaceName}";

            var instructions = $@"
To install the solution '{input.SolutionId}', please follow these steps:

1. Open Microsoft Sentinel Content Hub:
   {portalUrl}

2. Search for the solution:
   - In the search box, type: {GetSolutionDisplayName(input.SolutionId)}
   - Select the solution from the list

3. Click 'Install' or 'Create'

4. Follow the installation wizard:
   - Select your subscription: {input.SubscriptionId}
   - Select your resource group: {input.ResourceGroupName}
   - Select your workspace: {input.WorkspaceName}
   - Review and create

Alternative: Use Azure CLI or PowerShell for automated deployment.

For AWS connector specifically:
- Solution Name: Amazon Web Services
- After installation, configure:
  - AWS Role ARN
  - S3 bucket details
  - SQS queue URLs
";

            // Add specific configuration requirements based on solution type
            if (input.SolutionId.Contains("aws", StringComparison.OrdinalIgnoreCase) ||
                input.SolutionId.Contains("amazon", StringComparison.OrdinalIgnoreCase))
            {
                configurationRequired["AWS Role ARN"] = "Required for cross-account access";
                configurationRequired["S3 Bucket"] = "Required for log storage";
                configurationRequired["SQS Queue"] = "Required for event notifications";
            }

            return new InstallConnectorSolution.Output(
                Success: false,
                OperationId: string.Empty,
                Status: "ManualInstallationRequired",
                Message: instructions,
                InstalledComponents: installedComponents,
                EnabledDataConnectors: enabledDataConnectors,
                ConfigurationRequired: configurationRequired.Any() ? configurationRequired : null
            );
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogError(ex, "Authentication failed");
            return new InstallConnectorSolution.Output(
                Success: false,
                OperationId: string.Empty,
                Status: "AuthenticationFailed",
                Message: $"Authentication failed: {ex.Message}",
                InstalledComponents: installedComponents,
                EnabledDataConnectors: enabledDataConnectors,
                ConfigurationRequired: null
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error");
            return new InstallConnectorSolution.Output(
                Success: false,
                OperationId: string.Empty,
                Status: "Error",
                Message: $"Error: {ex.Message}",
                InstalledComponents: installedComponents,
                EnabledDataConnectors: enabledDataConnectors,
                ConfigurationRequired: null
            );
        }
    }

    private static string GetSolutionDisplayName(string solutionId)
    {
        // Map common solution IDs to display names
        if (solutionId.Contains("amazonwebservices", StringComparison.OrdinalIgnoreCase))
            return "Amazon Web Services";
        if (solutionId.Contains("office365", StringComparison.OrdinalIgnoreCase))
            return "Office 365";
        if (solutionId.Contains("azureactivity", StringComparison.OrdinalIgnoreCase))
            return "Azure Activity";
        if (solutionId.Contains("threatintelligence", StringComparison.OrdinalIgnoreCase))
            return "Threat Intelligence";

        // Default: clean up the ID
        return solutionId.Replace("azuresentinel.azure-sentinel-solution-", "")
            .Replace("-", " ")
            .Replace("_", " ");
    }
}