using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.OperationalInsights;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
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
            "Installing solution '{SolutionId}' in workspace '{WorkspaceName}'",
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

            // 3. Get access token for API calls
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

            // 5. Get the solution package to retrieve the ARM template
            _logger.LogInformation("Retrieving solution package for {SolutionId}", input.SolutionId);

            // Get product package details
            var packageUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                $"/resourceGroups/{input.ResourceGroupName}" +
                $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                $"/providers/Microsoft.SecurityInsights/contentProductPackages/{input.SolutionId}?api-version=2024-09-01";

            var packageResponse = await _httpClient.GetAsync(packageUrl, cancellationToken);

            if (!packageResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to retrieve package details for {SolutionId}. Trying template approach.", input.SolutionId);

                // Try to get the template directly
                var templateUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/contentProductTemplates/{input.SolutionId}?api-version=2024-09-01";

                packageResponse = await _httpClient.GetAsync(templateUrl, cancellationToken);
            }

            if (!packageResponse.IsSuccessStatusCode)
            {
                return new InstallConnectorSolution.Output(
                    Success: false,
                    OperationId: string.Empty,
                    Status: "PackageNotFound",
                    Message: $"Could not retrieve solution package for {input.SolutionId}. Error: {packageResponse.StatusCode}",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: null
                );
            }

            var packageContent = await packageResponse.Content.ReadAsStringAsync(cancellationToken);
            var packageData = JsonDocument.Parse(packageContent);

            // 6. Extract the mainTemplate from the package
            string? mainTemplate = null;
            if (packageData.RootElement.TryGetProperty("properties", out var packageProps) &&
                packageProps.TryGetProperty("mainTemplate", out var templateElement))
            {
                mainTemplate = templateElement.GetRawText();
            }

            if (string.IsNullOrEmpty(mainTemplate))
            {
                _logger.LogWarning("No mainTemplate found in package response. Attempting Content Hub installation approach.");

                // Try to install via Content Hub API directly
                var installUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/contentProductPackages/{input.SolutionId}" +
                    $"/install?api-version=2024-09-01";

                var installRequest = new HttpRequestMessage(HttpMethod.Post, installUrl);
                installRequest.Headers.Authorization = _httpClient.DefaultRequestHeaders.Authorization;

                var installResponse = await _httpClient.SendAsync(installRequest, cancellationToken);

                if (installResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Solution installation initiated via Content Hub for {SolutionId}", input.SolutionId);

                    return new InstallConnectorSolution.Output(
                        Success: true,
                        OperationId: input.SolutionId,
                        Status: "InstallationInitiated",
                        Message: $@"Solution installation has been initiated through Content Hub.

Please follow these steps to complete the setup:
1. Go to Azure Sentinel → Content Hub
2. Search for the solution and verify it shows as 'Installed'
3. Navigate to Data Connectors
4. Find and configure the AWS connector
5. You'll need:
   - AWS Role ARN (will be created in next steps)
   - SQS URL (will be created in next steps)
   - Destination table: Usually 'AWSCloudTrail'",
                        InstalledComponents: installedComponents,
                        EnabledDataConnectors: enabledDataConnectors,
                        ConfigurationRequired: new Dictionary<string, string>
                        {
                            ["AWS Role ARN"] = "Required - will be created in AWS setup",
                            ["SQS Queue URL"] = "Required - will be created in AWS setup",
                            ["Destination Table"] = "AWSCloudTrail (default)"
                        }
                    );
                }
                else
                {
                    var errorContent = await installResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Failed to install via Content Hub: {Error}", errorContent);

                    return new InstallConnectorSolution.Output(
                        Success: false,
                        OperationId: string.Empty,
                        Status: "ManualInstallationRequired",
                        Message: $@"Unable to install this solution programmatically.

Please install manually through the Azure Portal:
1. Navigate to Microsoft Sentinel → Content Hub
2. Search for '{input.SolutionId}'
3. Click on the solution and select 'Install'
4. Follow the installation wizard
5. Once installed, return here to continue with AWS configuration

Error details: {installResponse.StatusCode}",
                        InstalledComponents: installedComponents,
                        EnabledDataConnectors: enabledDataConnectors,
                        ConfigurationRequired: null
                    );
                }
            }

            // 7. Deploy the ARM template
            _logger.LogInformation("Deploying ARM template for solution {SolutionId}", input.SolutionId);

            var deploymentName = $"deploy-{input.SolutionId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var deploymentProperties = new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                Template = BinaryData.FromString(mainTemplate),
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    workspace = new
                    {
                        value = new
                        {
                            id = workspace.Value.Id.ToString(),
                            name = input.WorkspaceName,
                            location = workspace.Value.Data.Location.ToString()
                        }
                    },
                    location = new { value = workspace.Value.Data.Location.ToString() }
                })
            };

            var deploymentContent = new ArmDeploymentContent(deploymentProperties);

            // Deploy at resource group level
            var deploymentCollection = resourceGroup.Value.GetArmDeployments();
            var deploymentOperation = await deploymentCollection.CreateOrUpdateAsync(
                WaitUntil.Started,
                deploymentName,
                deploymentContent,
                cancellationToken);

            // Wait for deployment to complete (with timeout)
            var deploymentCompleted = await WaitForDeploymentAsync(
                deploymentOperation.Value,
                TimeSpan.FromMinutes(10),
                cancellationToken);

            if (!deploymentCompleted)
            {
                return new InstallConnectorSolution.Output(
                    Success: false,
                    OperationId: deploymentName,
                    Status: "DeploymentTimeout",
                    Message: "Deployment timed out. Check Azure portal for deployment status.",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: null
                );
            }

            // Check deployment status
            var deployment = deploymentOperation.Value;
            deployment = await deployment.GetAsync(cancellationToken);

            if (deployment.Data.Properties.ProvisioningState == ResourcesProvisioningState.Succeeded)
            {
                _logger.LogInformation("Successfully deployed solution {SolutionId}", input.SolutionId);

                // Add specific configuration requirements based on solution type
                if (input.SolutionId.Contains("aws", StringComparison.OrdinalIgnoreCase) ||
                    input.SolutionId.Contains("amazon", StringComparison.OrdinalIgnoreCase))
                {
                    configurationRequired["AWS Role ARN"] = "Required for cross-account access";
                    configurationRequired["S3 Bucket"] = "Required for log storage";
                    configurationRequired["SQS Queue"] = "Required for event notifications";
                }

                // Get outputs from deployment if any
                if (deployment.Data.Properties.Outputs != null)
                {
                    try
                    {
                        var outputs = deployment.Data.Properties.Outputs.ToObjectFromJson<Dictionary<string, object>>();
                        if (outputs != null)
                        {
                            foreach (var output in outputs)
                            {
                                installedComponents.Add($"Output: {output.Key}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse deployment outputs");
                    }
                }

                return new InstallConnectorSolution.Output(
                    Success: true,
                    OperationId: deploymentName,
                    Status: "Succeeded",
                    Message: $"Successfully installed solution {input.SolutionId}",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: configurationRequired.Count > 0 ? configurationRequired : null
                );
            }
            else
            {
                var errorMessage = deployment.Data.Properties.Error?.Message ?? "Deployment failed";
                return new InstallConnectorSolution.Output(
                    Success: false,
                    OperationId: deploymentName,
                    Status: deployment.Data.Properties.ProvisioningState?.ToString() ?? "Failed",
                    Message: $"Deployment failed: {errorMessage}",
                    InstalledComponents: installedComponents,
                    EnabledDataConnectors: enabledDataConnectors,
                    ConfigurationRequired: null
                );
            }
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
            _logger.LogError(ex, "Unexpected error installing solution");
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

    private async Task<bool> WaitForDeploymentAsync(
        ArmDeploymentResource deployment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < timeout)
        {
            deployment = await deployment.GetAsync(cancellationToken);

            var state = deployment.Data.Properties.ProvisioningState;
            if (state == ResourcesProvisioningState.Succeeded ||
                state == ResourcesProvisioningState.Failed ||
                state == ResourcesProvisioningState.Canceled)
            {
                return true;
            }

            _logger.LogDebug("Deployment {Name} is in state {State}. Waiting...",
                deployment.Data.Name,
                state);

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return false;
    }
}