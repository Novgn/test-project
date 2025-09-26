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

            _logger.LogInformation("Package API response: {StatusCode} for URL: {Url}", packageResponse.StatusCode, packageUrl);
            if (!packageResponse.IsSuccessStatusCode)
            {
                var errorContent = await packageResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to retrieve package details for {SolutionId}. Status: {StatusCode}, Error: {Error}",
                    input.SolutionId, packageResponse.StatusCode, errorContent);

                // Try to get the template directly
                var templateUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/contentProductTemplates/{input.SolutionId}?api-version=2024-09-01";

                packageResponse = await _httpClient.GetAsync(templateUrl, cancellationToken);

                _logger.LogInformation("Template API response: {StatusCode} for URL: {Url}", packageResponse.StatusCode, templateUrl);
                if (!packageResponse.IsSuccessStatusCode)
                {
                    var templateError = await packageResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Template API also failed. Status: {StatusCode}, Error: {Error}",
                        packageResponse.StatusCode, templateError);
                }
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

            // Log the first 500 characters of the response for debugging
            _logger.LogDebug("Package response content (first 500 chars): {Content}",
                packageContent.Length > 500 ? packageContent.Substring(0, 500) : packageContent);

            var packageData = JsonDocument.Parse(packageContent);

            // Check if the solution is already installed
            if (packageData.RootElement.TryGetProperty("properties", out var packageProps))
            {
                // Check installation status
                if (packageProps.TryGetProperty("isInstalled", out var installedElement) &&
                    installedElement.GetBoolean())
                {
                    _logger.LogInformation("Solution {SolutionId} is already installed", input.SolutionId);

                    // Get installed version and other details
                    string installedVersion = "Unknown";
                    if (packageProps.TryGetProperty("installedVersion", out var versionElement))
                    {
                        installedVersion = versionElement.GetString() ?? "Unknown";
                    }

                    // Get content items if available
                    if (packageProps.TryGetProperty("contentItems", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("contentKind", out var kind) &&
                                item.TryGetProperty("displayName", out var name))
                            {
                                var component = $"{kind.GetString()}: {name.GetString()}";
                                installedComponents.Add(component);

                                // Track data connectors
                                if (kind.GetString()?.Equals("DataConnector", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    enabledDataConnectors.Add(name.GetString() ?? "Unknown");
                                }
                            }
                        }
                    }

                    return new InstallConnectorSolution.Output(
                        Success: true,
                        OperationId: string.Empty,
                        Status: "AlreadyInstalled",
                        Message: $@"Solution {input.SolutionId} (v{installedVersion}) is already installed in the workspace.

Next steps to configure the AWS data connector:
1. Navigate to Microsoft Sentinel → Data Connectors
2. Search for 'AWS' or 'Amazon Web Services S3'
3. Click on the connector to open its configuration page
4. Follow the setup wizard to configure:
   - AWS Role ARN (needs to be created in AWS)
   - S3 Bucket and SQS Queue settings
   - Log types to ingest (e.g., CloudTrail, VPC Flow Logs, GuardDuty)",
                        InstalledComponents: installedComponents,
                        EnabledDataConnectors: enabledDataConnectors,
                        ConfigurationRequired: new Dictionary<string, string>
                        {
                            ["AWS Role ARN"] = "Required - create via AWS setup script",
                            ["S3 Bucket"] = "Required - for log storage",
                            ["SQS Queue URL"] = "Required - for event notifications",
                            ["Destination Table"] = "AWSCloudTrail (for CloudTrail logs)"
                        }
                    );
                }
            }

            // 6. Extract the mainTemplate from the package
            string? mainTemplate = null;
            if (packageData.RootElement.TryGetProperty("properties", out var packageProps2) &&
                packageProps2.TryGetProperty("mainTemplate", out var templateElement))
            {
                mainTemplate = templateElement.GetRawText();
            }

            if (string.IsNullOrEmpty(mainTemplate))
            {
                _logger.LogWarning("No mainTemplate found in package response. Attempting Content Hub installation approach.");

                // Try to install via Content Hub API directly
                // First, try with the solution ID as-is
                var installUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                    $"/resourceGroups/{input.ResourceGroupName}" +
                    $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                    $"/providers/Microsoft.SecurityInsights/contentProductPackages/{input.SolutionId}" +
                    $"/install?api-version=2024-09-01";

                var installRequest = new HttpRequestMessage(HttpMethod.Post, installUrl);
                installRequest.Headers.Authorization = _httpClient.DefaultRequestHeaders.Authorization;

                // Add Content-Type header for the POST request
                installRequest.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                var installResponse = await _httpClient.SendAsync(installRequest, cancellationToken);

                _logger.LogInformation("Content Hub install API response: {StatusCode} for URL: {Url}",
                    installResponse.StatusCode, installUrl);

                // If the first attempt fails with NotFound, try alternative solution ID formats
                if (installResponse.StatusCode == System.Net.HttpStatusCode.NotFound &&
                    input.SolutionId.Contains("azuresentinel.azure-sentinel-solution-"))
                {
                    _logger.LogInformation("Trying alternative solution ID format");

                    // Try with just the last part of the solution ID
                    var alternativeId = input.SolutionId.Replace("azuresentinel.azure-sentinel-solution-", "");
                    installUrl = $"https://management.azure.com/subscriptions/{input.SubscriptionId}" +
                        $"/resourceGroups/{input.ResourceGroupName}" +
                        $"/providers/Microsoft.OperationalInsights/workspaces/{input.WorkspaceName}" +
                        $"/providers/Microsoft.SecurityInsights/contentProductPackages/{alternativeId}" +
                        $"/install?api-version=2024-09-01";

                    installRequest = new HttpRequestMessage(HttpMethod.Post, installUrl);
                    installRequest.Headers.Authorization = _httpClient.DefaultRequestHeaders.Authorization;
                    installRequest.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                    installResponse = await _httpClient.SendAsync(installRequest, cancellationToken);
                    _logger.LogInformation("Alternative format response: {StatusCode}", installResponse.StatusCode);
                }

                if (installResponse.IsSuccessStatusCode ||
                    installResponse.StatusCode == System.Net.HttpStatusCode.Accepted)
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
                    _logger.LogError("Failed to install via Content Hub. Status: {StatusCode}, Error: {Error}",
                        installResponse.StatusCode, errorContent);

                    // Check if it's a specific error we can provide guidance for
                    var errorMessage = "Unable to install this solution programmatically.";
                    if (errorContent.Contains("already installed", StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = "This solution appears to be already installed.";
                    }
                    else if (installResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        errorMessage = "Solution not found in Content Hub. It may require a different ID or manual installation.";
                    }

                    return new InstallConnectorSolution.Output(
                        Success: false,
                        OperationId: string.Empty,
                        Status: "ManualInstallationRequired",
                        Message: $@"{errorMessage}

Please install manually through the Azure Portal:
1. Navigate to Microsoft Sentinel → Content Hub
2. Search for 'Amazon Web Services' or 'AWS'
3. Click on the solution and select 'Install'
4. Follow the installation wizard
5. Once installed, return here to continue with AWS configuration

Error details: {installResponse.StatusCode} - {errorContent}",
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