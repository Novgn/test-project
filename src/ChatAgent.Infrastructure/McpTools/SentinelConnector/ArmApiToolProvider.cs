using ChatAgent.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Core;

namespace ChatAgent.Infrastructure.McpTools.SentinelConnector;

public class ArmApiToolProvider : IMcpToolProvider
{
    private readonly ILogger<ArmApiToolProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;

    public string Name => "arm-api";
    public string Description => "Azure Resource Manager API tools for deploying and managing Azure Sentinel connector solutions";

    public ArmApiToolProvider(ILogger<ArmApiToolProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("ArmApi");
        _httpClient.BaseAddress = new Uri("https://management.azure.com/");
        _credential = new DefaultAzureCredential();
    }

    public async Task<List<ToolDescriptor>> GetAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        return new List<ToolDescriptor>
        {
            new ToolDescriptor
            {
                Name = "DeployAwsConnectorSolution",
                Description = "Deploy the AWS connector solution to Azure Sentinel workspace",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["subscriptionId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure subscription ID",
                        Required = true
                    },
                    ["resourceGroupName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Resource group name containing the Sentinel workspace",
                        Required = true
                    },
                    ["workspaceName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Log Analytics workspace name",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "ConfigureAwsDataConnector",
                Description = "Configure the AWS data connector with role ARN and SQS URL",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["subscriptionId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure subscription ID",
                        Required = true
                    },
                    ["resourceGroupName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Resource group name",
                        Required = true
                    },
                    ["workspaceName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Log Analytics workspace name",
                        Required = true
                    },
                    ["roleArn"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS IAM Role ARN for authentication",
                        Required = true
                    },
                    ["sqsUrl"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS SQS Queue URL for log ingestion",
                        Required = true
                    },
                    ["destinationTable"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Destination table for logs (e.g., AWSCloudTrail, AWSVPCFlow, AWSGuardDuty)",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "CheckConnectorStatus",
                Description = "Check the status of AWS data connector",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["subscriptionId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure subscription ID",
                        Required = true
                    },
                    ["resourceGroupName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Resource group name",
                        Required = true
                    },
                    ["workspaceName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Log Analytics workspace name",
                        Required = true
                    },
                    ["connectorId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Data connector ID",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "ListDataConnectors",
                Description = "List all data connectors in the Sentinel workspace",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["subscriptionId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure subscription ID",
                        Required = true
                    },
                    ["resourceGroupName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Resource group name",
                        Required = true
                    },
                    ["workspaceName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Log Analytics workspace name",
                        Required = true
                    }
                }
            }
        };
    }

    public async Task<object> ExecuteAsync(string toolName, Dictionary<string, object> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticatedAsync(cancellationToken);

            return toolName switch
            {
                "DeployAwsConnectorSolution" => await DeployAwsConnectorSolution(parameters, cancellationToken),
                "ConfigureAwsDataConnector" => await ConfigureAwsDataConnector(parameters, cancellationToken),
                "CheckConnectorStatus" => await CheckConnectorStatus(parameters, cancellationToken),
                "ListDataConnectors" => await ListDataConnectors(parameters, cancellationToken),
                _ => throw new NotSupportedException($"Tool {toolName} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing ARM API tool {ToolName}", toolName);
            throw;
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var tokenRequest = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
        var token = await _credential.GetTokenAsync(tokenRequest, cancellationToken);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private async Task<object> DeployAwsConnectorSolution(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var subscriptionId = parameters["subscriptionId"].ToString();
        var resourceGroupName = parameters["resourceGroupName"].ToString();

        // Handle both workspaceName and workspaceId parameters, with hardcoded default for testing
        string workspaceName;
        if (parameters.ContainsKey("workspaceName"))
        {
            workspaceName = parameters["workspaceName"].ToString()!;
        }
        else if (parameters.ContainsKey("workspaceId"))
        {
            // Extract workspace name from workspace ID if provided
            // Workspace IDs typically have format: /subscriptions/{sub}/resourcegroups/{rg}/providers/microsoft.operationalinsights/workspaces/{name}
            var workspaceId = parameters["workspaceId"].ToString()!;
            if (workspaceId.Contains("/workspaces/"))
            {
                workspaceName = workspaceId.Split("/workspaces/").Last();
            }
            else
            {
                // Fallback to hardcoded value for testing
                workspaceName = "test-workspace";
                _logger.LogWarning("Using hardcoded workspace name: {WorkspaceName}", workspaceName);
            }
        }
        else
        {
            // Hardcoded default for testing
            workspaceName = "test-workspace";
            _logger.LogWarning("No workspace name or ID provided, using default: {WorkspaceName}", workspaceName);
        }

        // Deploy the AWS solution from Content Hub
        var deploymentName = $"aws-connector-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var deploymentUri = $"subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/{deploymentName}?api-version=2021-04-01";

        var deploymentBody = new
        {
            properties = new
            {
                mode = "Incremental",
                template = new
                {
                    schema = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                    contentVersion = "1.0.0.0",
                    resources = new[]
                    {
                        new
                        {
                            type = "Microsoft.OperationalInsights/workspaces/providers/contentPackages",
                            apiVersion = "2023-04-01-preview",
                            name = $"{workspaceName}/Microsoft.SecurityInsights/AmazonWebServicesS3",
                            location = "[resourceGroup().location]",
                            properties = new
                            {
                                version = "3.0.0",
                                kind = "Solution",
                                contentSchemaVersion = "3.0.0",
                                displayName = "Amazon Web Services",
                                publisherDisplayName = "Microsoft Sentinel, Microsoft Corporation",
                                descriptionHtml = "AWS data connector for Microsoft Sentinel",
                                contentKind = "Solution",
                                contentProductId = "azuresentinel.azure-sentinel-solution-amazonwebservices",
                                id = "azuresentinel.azure-sentinel-solution-amazonwebservices",
                                contentId = "azuresentinel.azure-sentinel-solution-amazonwebservices"
                            }
                        }
                    }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(deploymentBody), Encoding.UTF8, "application/json");
        var response = await _httpClient.PutAsync(deploymentUri, content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return new
            {
                success = true,
                deploymentName = deploymentName,
                message = "AWS connector solution deployed successfully",
                details = JsonSerializer.Deserialize<object>(responseContent)
            };
        }

        throw new Exception($"Failed to deploy AWS connector solution: {response.StatusCode} - {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    private async Task<object> ConfigureAwsDataConnector(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var subscriptionId = parameters["subscriptionId"].ToString();
        var resourceGroupName = parameters["resourceGroupName"].ToString();
        var workspaceName = parameters["workspaceName"].ToString();
        var roleArn = parameters["roleArn"].ToString();
        var sqsUrl = parameters["sqsUrl"].ToString();
        var destinationTable = parameters["destinationTable"].ToString();

        var connectorName = $"AWS_{destinationTable}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var connectorUri = $"subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.OperationalInsights/workspaces/{workspaceName}/providers/Microsoft.SecurityInsights/dataConnectors/{connectorName}?api-version=2023-02-01";

        var connectorBody = new
        {
            kind = "AmazonWebServicesS3",
            properties = new
            {
                destinationTable = destinationTable,
                roleArn = roleArn,
                sqsUrls = new[] { sqsUrl },
                dataTypes = new
                {
                    logs = new
                    {
                        state = "Enabled"
                    }
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(connectorBody), Encoding.UTF8, "application/json");
        var response = await _httpClient.PutAsync(connectorUri, content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return new
            {
                success = true,
                connectorName = connectorName,
                message = "AWS data connector configured successfully",
                details = JsonSerializer.Deserialize<object>(responseContent)
            };
        }

        throw new Exception($"Failed to configure AWS data connector: {response.StatusCode} - {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    private async Task<object> CheckConnectorStatus(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var subscriptionId = parameters["subscriptionId"].ToString();
        var resourceGroupName = parameters["resourceGroupName"].ToString();
        var workspaceName = parameters["workspaceName"].ToString();
        var connectorId = parameters["connectorId"].ToString();

        var statusUri = $"subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.OperationalInsights/workspaces/{workspaceName}/providers/Microsoft.SecurityInsights/dataConnectors/{connectorId}?api-version=2023-02-01";

        var response = await _httpClient.GetAsync(statusUri, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var connectorStatus = JsonSerializer.Deserialize<JsonElement>(responseContent);

            return new
            {
                success = true,
                connectorId = connectorId,
                status = connectorStatus.GetProperty("properties").GetProperty("dataTypes").GetProperty("logs").GetProperty("state").GetString(),
                details = connectorStatus
            };
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new
            {
                success = false,
                connectorId = connectorId,
                status = "NotFound",
                message = "Data connector not found"
            };
        }

        throw new Exception($"Failed to check connector status: {response.StatusCode} - {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    private async Task<object> ListDataConnectors(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var subscriptionId = parameters["subscriptionId"].ToString();
        var resourceGroupName = parameters["resourceGroupName"].ToString();
        var workspaceName = parameters["workspaceName"].ToString();

        var listUri = $"subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.OperationalInsights/workspaces/{workspaceName}/providers/Microsoft.SecurityInsights/dataConnectors?api-version=2023-02-01";

        var response = await _httpClient.GetAsync(listUri, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var connectors = JsonSerializer.Deserialize<JsonElement>(responseContent);

            var connectorList = new List<object>();
            if (connectors.TryGetProperty("value", out var value))
            {
                foreach (var connector in value.EnumerateArray())
                {
                    connectorList.Add(new
                    {
                        name = connector.GetProperty("name").GetString(),
                        kind = connector.GetProperty("kind").GetString(),
                        id = connector.GetProperty("id").GetString()
                    });
                }
            }

            return new
            {
                success = true,
                connectors = connectorList,
                count = connectorList.Count
            };
        }

        throw new Exception($"Failed to list data connectors: {response.StatusCode} - {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }
}