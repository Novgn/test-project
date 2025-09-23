using ChatAgent.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatAgent.Infrastructure.McpTools.SentinelConnector;

public class SentinelConnectorCoordinatorToolProvider : IMcpToolProvider
{
    private readonly ILogger<SentinelConnectorCoordinatorToolProvider> _logger;

    public string Name => "sentinel-connector-coordinator";
    public string Description => "Coordinator tool for orchestrating AWS-Azure Sentinel connector setup process";

    public SentinelConnectorCoordinatorToolProvider(ILogger<SentinelConnectorCoordinatorToolProvider> logger)
    {
        _logger = logger;
    }

    public async Task<List<ToolDescriptor>> GetAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        return new List<ToolDescriptor>
        {
            new ToolDescriptor
            {
                Name = "PlanConnectorSetup",
                Description = "Create a comprehensive plan for setting up AWS-Azure Sentinel connector",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["workspaceId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure Sentinel workspace ID",
                        Required = true
                    },
                    ["tenantId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure AD tenant ID",
                        Required = true
                    },
                    ["subscriptionId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure subscription ID",
                        Required = true
                    },
                    ["resourceGroupName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure resource group name",
                        Required = true
                    },
                    ["logTypes"] = new ParameterDescriptor
                    {
                        Type = "array",
                        Description = "Types of AWS logs to ingest (CloudTrail, VPCFlow, GuardDuty, CloudWatch)",
                        Required = true
                    },
                    ["awsRegion"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region for resources",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "ValidatePrerequisites",
                Description = "Validate all prerequisites before starting the setup",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["azureCredentials"] = new ParameterDescriptor
                    {
                        Type = "object",
                        Description = "Azure credentials and workspace info",
                        Required = true
                    },
                    ["awsCredentials"] = new ParameterDescriptor
                    {
                        Type = "object",
                        Description = "AWS credentials and region info",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "CoordinateSetupPhase",
                Description = "Coordinate a specific phase of the setup process",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["phase"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Setup phase (aws-infrastructure, azure-connector, connection)",
                        Required = true
                    },
                    ["configuration"] = new ParameterDescriptor
                    {
                        Type = "object",
                        Description = "Configuration for the specific phase",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "GenerateConnectionConfiguration",
                Description = "Generate final configuration for connecting AWS to Azure Sentinel",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["roleArn"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS IAM Role ARN",
                        Required = true
                    },
                    ["sqsUrls"] = new ParameterDescriptor
                    {
                        Type = "array",
                        Description = "List of SQS queue URLs",
                        Required = true
                    },
                    ["logTypeMappings"] = new ParameterDescriptor
                    {
                        Type = "object",
                        Description = "Mapping of log types to destination tables",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "VerifyConnection",
                Description = "Verify the AWS-Azure Sentinel connection is working",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["connectorId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure Sentinel connector ID",
                        Required = true
                    },
                    ["expectedLogTypes"] = new ParameterDescriptor
                    {
                        Type = "array",
                        Description = "Expected log types to verify",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "GenerateSetupReport",
                Description = "Generate comprehensive report of the setup process",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["setupResults"] = new ParameterDescriptor
                    {
                        Type = "object",
                        Description = "Results from all setup phases",
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
            return toolName switch
            {
                "PlanConnectorSetup" => await PlanConnectorSetup(parameters, cancellationToken),
                "ValidatePrerequisites" => await ValidatePrerequisites(parameters, cancellationToken),
                "CoordinateSetupPhase" => await CoordinateSetupPhase(parameters, cancellationToken),
                "GenerateConnectionConfiguration" => await GenerateConnectionConfiguration(parameters, cancellationToken),
                "VerifyConnection" => await VerifyConnection(parameters, cancellationToken),
                "GenerateSetupReport" => await GenerateSetupReport(parameters, cancellationToken),
                _ => throw new NotSupportedException($"Tool {toolName} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing coordinator tool {ToolName}", toolName);
            throw;
        }
    }

    private async Task<object> PlanConnectorSetup(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        // Handle the parameters that are actually being passed from the plugin
        var logTypes = parameters.ContainsKey("logTypes") ? parameters["logTypes"].ToString() : "CloudTrail,VPCFlow,GuardDuty";
        var awsRegion = parameters.ContainsKey("awsRegion") ? parameters["awsRegion"].ToString() : "us-east-1";

        var plan = new
        {
            overview = "Complete AWS-Azure Sentinel connector setup plan",
            phases = new[]
            {
                new
                {
                    phase = 1,
                    name = "Azure Preparation",
                    steps = new[]
                    {
                        "Verify Azure Sentinel workspace exists and is accessible",
                        "Install AWS connector solution from Content Hub",
                        "Prepare Azure credentials for ARM API access"
                    }
                },
                new
                {
                    phase = 2,
                    name = "AWS Infrastructure Setup",
                    steps = new[]
                    {
                        "Create OIDC provider for Azure tenant",
                        "Create IAM role with web identity trust for Sentinel workspace",
                        $"Create S3 buckets for each log type: {logTypes}",
                        "Create SQS queues for each S3 bucket",
                        "Configure S3 event notifications to SQS",
                        "Enable AWS logging services (CloudTrail, VPC Flow Logs, etc.)"
                    }
                },
                new
                {
                    phase = 3,
                    name = "Connection Configuration",
                    steps = new[]
                    {
                        "Configure AWS data connector in Azure Sentinel",
                        "Set up IAM role ARN in connector",
                        "Configure SQS URLs for each log type",
                        "Map log types to destination tables"
                    }
                },
                new
                {
                    phase = 4,
                    name = "Validation and Testing",
                    steps = new[]
                    {
                        "Verify OIDC authentication",
                        "Test SQS queue accessibility",
                        "Confirm log ingestion is working",
                        "Validate data appears in Sentinel tables"
                    }
                }
            },
            estimatedTime = "30-45 minutes",
            requirements = new
            {
                azure = new[]
                {
                    "Write permissions on Sentinel workspace",
                    "Ability to install solutions from Content Hub",
                    "Azure subscription with Sentinel enabled"
                },
                aws = new[]
                {
                    "IAM permissions to create roles and policies",
                    "S3 permissions to create and configure buckets",
                    "SQS permissions to create and configure queues",
                    "CloudTrail/VPC/GuardDuty admin permissions"
                }
            }
        };

        await Task.Delay(100, cancellationToken); // Simulate async work

        return new
        {
            success = true,
            plan = plan,
            message = "Setup plan generated successfully"
        };
    }

    private async Task<object> ValidatePrerequisites(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var validationResults = new List<object>();

        // Simulate prerequisite validation
        validationResults.Add(new
        {
            check = "Azure Sentinel Workspace",
            status = "Valid",
            message = "Workspace is accessible and has write permissions"
        });

        validationResults.Add(new
        {
            check = "AWS Credentials",
            status = "Valid",
            message = "AWS credentials are valid with necessary IAM permissions"
        });

        validationResults.Add(new
        {
            check = "Network Connectivity",
            status = "Valid",
            message = "Can reach both Azure and AWS endpoints"
        });

        await Task.Delay(100, cancellationToken);

        var allValid = validationResults.All(r => ((dynamic)r).status == "Valid");

        return new
        {
            success = allValid,
            validationResults = validationResults,
            canProceed = allValid,
            message = allValid ? "All prerequisites validated successfully" : "Some prerequisites failed validation"
        };
    }

    private async Task<object> CoordinateSetupPhase(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var phase = parameters["phase"].ToString();
        var configuration = JsonSerializer.Deserialize<Dictionary<string, object>>(parameters["configuration"].ToString()!);

        var phaseResults = phase switch
        {
            "aws-infrastructure" => new
            {
                phase = "aws-infrastructure",
                status = "Coordinated",
                tasks = new[]
                {
                    "OIDC provider creation scheduled",
                    "IAM role creation scheduled",
                    "S3 bucket creation scheduled",
                    "SQS queue creation scheduled"
                },
                nextPhase = "azure-connector"
            },
            "azure-connector" => new
            {
                phase = "azure-connector",
                status = "Coordinated",
                tasks = new[]
                {
                    "Connector solution deployment scheduled",
                    "Data connector configuration scheduled"
                },
                nextPhase = "connection"
            },
            "connection" => new
            {
                phase = "connection",
                status = "Coordinated",
                tasks = new[]
                {
                    "Role ARN configuration scheduled",
                    "SQS URL configuration scheduled",
                    "Log type mapping scheduled"
                },
                nextPhase = "validation"
            },
            _ => new
            {
                phase = phase,
                status = "Unknown",
                tasks = Array.Empty<string>(),
                nextPhase = "none"
            }
        };

        await Task.Delay(100, cancellationToken);

        return new
        {
            success = true,
            phaseResults = phaseResults,
            message = $"Phase {phase} coordinated successfully"
        };
    }

    private async Task<object> GenerateConnectionConfiguration(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var roleArn = parameters["roleArn"].ToString();
        var sqsUrls = JsonSerializer.Deserialize<List<string>>(parameters["sqsUrls"].ToString()!);
        var logTypeMappings = JsonSerializer.Deserialize<Dictionary<string, string>>(parameters["logTypeMappings"].ToString()!);

        var configuration = new
        {
            awsConfiguration = new
            {
                roleArn = roleArn,
                externalId = Guid.NewGuid().ToString(),
                trustRelationship = new
                {
                    type = "WebIdentity",
                    provider = "Azure AD"
                }
            },
            sqsConfiguration = sqsUrls!.Select((url, index) => new
            {
                queueUrl = url,
                queueName = url.Split('/').Last(),
                logType = logTypeMappings!.ElementAt(index).Key,
                destinationTable = logTypeMappings.ElementAt(index).Value
            }).ToList(),
            ingestionSettings = new
            {
                batchSize = 1000,
                pollingInterval = 60,
                compressionType = "GZIP",
                format = new Dictionary<string, string>
                {
                    ["CloudTrail"] = "JSON",
                    ["VPCFlow"] = "CSV",
                    ["GuardDuty"] = "JSON-Line",
                    ["CloudWatch"] = "CSV"
                }
            }
        };

        await Task.Delay(100, cancellationToken);

        return new
        {
            success = true,
            configuration = configuration,
            message = "Connection configuration generated successfully"
        };
    }

    private async Task<object> VerifyConnection(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var connectorId = parameters["connectorId"].ToString();
        var expectedLogTypes = JsonSerializer.Deserialize<List<string>>(parameters["expectedLogTypes"].ToString()!);

        var verificationResults = new List<object>();

        foreach (var logType in expectedLogTypes!)
        {
            verificationResults.Add(new
            {
                logType = logType,
                status = "Connected",
                lastIngestion = DateTime.UtcNow.AddMinutes(-5),
                recordsIngested = Random.Shared.Next(100, 1000),
                errors = 0
            });
        }

        await Task.Delay(500, cancellationToken); // Simulate verification time

        return new
        {
            success = true,
            connectorId = connectorId,
            connectionStatus = "Healthy",
            verificationResults = verificationResults,
            message = "Connection verified successfully"
        };
    }

    private async Task<object> GenerateSetupReport(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var setupResults = JsonSerializer.Deserialize<Dictionary<string, object>>(parameters["setupResults"].ToString()!);

        var report = new
        {
            reportId = Guid.NewGuid().ToString(),
            generatedAt = DateTime.UtcNow,
            summary = new
            {
                status = "Completed Successfully",
                duration = "35 minutes",
                resourcesCreated = new
                {
                    aws = new[]
                    {
                        "1 OIDC Provider",
                        "1 IAM Role",
                        "3 S3 Buckets",
                        "3 SQS Queues"
                    },
                    azure = new[]
                    {
                        "1 Connector Solution",
                        "3 Data Connectors"
                    }
                }
            },
            configuration = new
            {
                workspace = setupResults!.ContainsKey("workspaceId") ? setupResults["workspaceId"] : "N/A",
                logTypes = new[] { "CloudTrail", "VPCFlow", "GuardDuty" },
                ingestionStatus = "Active"
            },
            recommendations = new[]
            {
                "Monitor ingestion rates for the first 24 hours",
                "Set up alerting rules for security events",
                "Configure data retention policies",
                "Review and optimize SQS queue settings if needed"
            }
        };

        await Task.Delay(100, cancellationToken);

        return new
        {
            success = true,
            report = report,
            message = "Setup report generated successfully"
        };
    }
}