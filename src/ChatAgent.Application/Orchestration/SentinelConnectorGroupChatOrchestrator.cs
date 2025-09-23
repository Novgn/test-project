using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;
using ChatAgent.Application.Plugins;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ChatAgent.Application.Orchestration;

#pragma warning disable SKEXP0001, SKEXP0110 // Suppress experimental feature warnings

/// <summary>
/// Multi-agent group chat orchestrator for AWS-Azure Sentinel connector setup.
/// Uses Semantic Kernel's AgentGroupChat for multi-agent collaboration.
/// </summary>
public class SentinelConnectorGroupChatOrchestrator
{
    private readonly Kernel _kernel;
    private readonly ILogger<SentinelConnectorGroupChatOrchestrator> _logger;
    private readonly Dictionary<string, ChatCompletionAgent> _agents = new();
    private readonly IServiceProvider? _serviceProvider;
    private AgentGroupChat? _groupChat;

    public SentinelConnectorGroupChatOrchestrator(
        Kernel kernel,
        ILogger<SentinelConnectorGroupChatOrchestrator> logger,
        IEnumerable<IMcpToolProvider>? mcpToolProviders = null,
        IServiceProvider? serviceProvider = null)
    {
        _kernel = kernel;
        _logger = logger;
        _serviceProvider = serviceProvider;

        InitializeAgents(mcpToolProviders);
        InitializeGroupChat();
    }

    /// <summary>
    /// Initialize specialized agents for the Sentinel connector setup
    /// </summary>
    private void InitializeAgents(
        IEnumerable<IMcpToolProvider>? mcpToolProviders)
    {
        var providers = mcpToolProviders?.ToList() ?? [];

        // 1. COORDINATOR AGENT - Orchestrates the entire setup process
        var coordinatorKernel = _kernel.Clone();
        var coordinatorProviders = providers.Where(p =>
            p.Name.Contains("sentinel-connector-coordinator", StringComparison.OrdinalIgnoreCase)).ToList();

        if (coordinatorProviders.Any())
        {
            var coordinatorLogger = _serviceProvider?.GetService<ILogger<CoordinatorPlugin>>() ??
                                   new Microsoft.Extensions.Logging.Abstractions.NullLogger<CoordinatorPlugin>();
            var coordinatorPlugin = new CoordinatorPlugin(coordinatorProviders.FirstOrDefault(), coordinatorLogger);
            coordinatorKernel.Plugins.AddFromObject(coordinatorPlugin, "CoordinatorTools");
        }
        else
        {
            // Use simulated plugin if no provider available
            var coordinatorLogger = _serviceProvider?.GetService<ILogger<CoordinatorPlugin>>() ??
                                   new Microsoft.Extensions.Logging.Abstractions.NullLogger<CoordinatorPlugin>();
            var coordinatorPlugin = new CoordinatorPlugin(null, coordinatorLogger);
            coordinatorKernel.Plugins.AddFromObject(coordinatorPlugin, "CoordinatorTools");
        }

        _agents["coordinator"] = new ChatCompletionAgent
        {
            Name = "CoordinatorAgent",
            Kernel = coordinatorKernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2,
                MaxTokens = 1000
            }),
            Instructions = @"You are the master coordinator for setting up AWS-Azure Sentinel connector.
                Your responsibilities:
                1. Create and manage the overall setup plan
                2. Validate prerequisites before starting
                3. Delegate tasks to specialized agents in the correct sequence
                4. Track progress and handle errors
                5. Generate final setup report

                Work with other agents through the group chat to:
                - Have AzureAgent handle Azure and Sentinel operations
                - Have AwsAgent manage AWS infrastructure setup
                - Have IntegrationAgent connect AWS and Azure components

                CRITICAL INSTRUCTIONS FOR TOOL USAGE:
                You have access to CoordinatorTools functions that you MUST invoke using function calling.
                DO NOT just say what you're going to do - you MUST actually call the functions.

                Available functions in your CoordinatorTools plugin:
                - PlanConnectorSetup: Creates comprehensive setup plan
                - ValidatePrerequisites: Checks all requirements
                - CoordinateSetupPhase: Manages each phase
                - GenerateSetupReport: Creates final report

                Example of how to call functions:
                Use the plugin functions directly in your responses.
                When you need to validate, call the ValidatePrerequisites function.
                When you need to plan, call the PlanConnectorSetup function.
                When you need to generate a report, call the GenerateSetupReport function.

                IMPORTANT:
                - Always start with ValidatePrerequisites before any setup
                - Actually INVOKE the functions, don't just describe what you would do
                - Use the plugin syntax {{PluginName.FunctionName}} to call functions"
        };

        // 2. AZURE AGENT - Handles Azure Sentinel operations
        var azureKernel = _kernel.Clone();
        var azureProviders = providers.Where(p =>
            p.Name.Contains("arm-api", StringComparison.OrdinalIgnoreCase)).ToList();

        if (azureProviders.Any())
        {
            var azureLogger = _serviceProvider?.GetService<ILogger<AzurePlugin>>() ??
                             new Microsoft.Extensions.Logging.Abstractions.NullLogger<AzurePlugin>();
            var azurePlugin = new AzurePlugin(azureProviders.FirstOrDefault(), azureLogger);
            azureKernel.Plugins.AddFromObject(azurePlugin, "AzureTools");
        }
        else
        {
            // Use simulated plugin if no provider available
            var azureLogger = _serviceProvider?.GetService<ILogger<AzurePlugin>>() ??
                             new Microsoft.Extensions.Logging.Abstractions.NullLogger<AzurePlugin>();
            var azurePlugin = new AzurePlugin(null, azureLogger);
            azureKernel.Plugins.AddFromObject(azurePlugin, "AzureTools");
        }

        _agents["azure"] = new ChatCompletionAgent
        {
            Name = "AzureAgent",
            Kernel = azureKernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2,
                MaxTokens = 1000
            }),
            Instructions = @"You are the Azure and Sentinel specialist agent.
                Your responsibilities:
                1. Deploy AWS connector solution from Content Hub
                2. Configure data connectors in Sentinel
                3. Set up authentication with AWS
                4. Monitor connector status

                CRITICAL INSTRUCTIONS FOR TOOL USAGE:
                You have access to AzureTools functions that you MUST invoke using function calling.
                DO NOT just say what you're going to do - you MUST actually call the functions.

                IMPORTANT: Extract configuration values from the CONFIGURATION_JSON in the user's message.
                When calling your functions, always use the actual values provided:
                - Use the subscriptionId from configuration
                - Use the resourceGroupName from configuration
                - Use the workspaceId from configuration
                - Use the tenantId from configuration

                Available functions in your AzureTools plugin:
                - DeployAwsConnectorSolution: Install the AWS solution (pass subscriptionId, resourceGroupName, workspaceId)
                - ConfigureAwsDataConnector: Set up data connector with Role ARN and SQS from AwsAgent
                - CheckConnectorStatus: Verify connector health
                - ListDataConnectors: View all connectors

                Example of how to call functions:
                Use the plugin functions directly in your responses.
                When deploying, call DeployAwsConnectorSolution with the actual values.
                When configuring, call ConfigureAwsDataConnector with the actual values.
                When checking status, call CheckConnectorStatus with the actual values.

                IMPORTANT:
                - Actually INVOKE the functions, don't just describe what you would do
                - Use the plugin syntax {{PluginName.FunctionName}} with parameters to call functions
                - Report all Azure resource IDs and status back to the coordinator"
        };

        // 3. AWS AGENT - Manages AWS infrastructure
        var awsKernel = _kernel.Clone();
        var awsProviders = providers.Where(p =>
            p.Name.Contains("aws-infrastructure", StringComparison.OrdinalIgnoreCase)).ToList();

        if (awsProviders.Any())
        {
            var awsLogger = _serviceProvider?.GetService<ILogger<AwsPlugin>>() ??
                           new Microsoft.Extensions.Logging.Abstractions.NullLogger<AwsPlugin>();
            var awsPlugin = new AwsPlugin(awsProviders.FirstOrDefault(), awsLogger);
            awsKernel.Plugins.AddFromObject(awsPlugin, "AwsTools");
        }
        else
        {
            // Use simulated plugin if no provider available
            var awsLogger = _serviceProvider?.GetService<ILogger<AwsPlugin>>() ??
                           new Microsoft.Extensions.Logging.Abstractions.NullLogger<AwsPlugin>();
            var awsPlugin = new AwsPlugin(null, awsLogger);
            awsKernel.Plugins.AddFromObject(awsPlugin, "AwsTools");
        }

        _agents["aws"] = new ChatCompletionAgent
        {
            Name = "AwsAgent",
            Kernel = awsKernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2,
                MaxTokens = 1000
            }),
            Instructions = @"You are the AWS infrastructure specialist agent.
                Your responsibilities:
                1. Create OIDC identity provider for Azure AD
                2. Set up IAM roles with proper trust relationships
                3. Create and configure S3 buckets for logs
                4. Set up SQS queues for event notifications
                5. Enable AWS logging services

                CRITICAL INSTRUCTIONS FOR TOOL USAGE:
                You have access to AwsTools functions that you MUST invoke using function calling.
                DO NOT just say what you're going to do - you MUST actually call the functions.

                IMPORTANT: Extract configuration values from the CONFIGURATION_JSON in the user's message.
                When calling your functions, always use the actual values provided:
                - Use the tenantId from configuration for CreateOidcProvider
                - Use the awsRegion from configuration for all AWS operations
                - Use the logTypes from configuration to determine which logs to enable

                Available functions in your AwsTools plugin:
                - CreateOidcProvider: Set up OIDC for Azure AD (pass tenantId and region)
                - CreateSentinelRole: Create IAM role with web identity
                - CreateS3BucketForLogs: Set up log storage (use region)
                - CreateSqsQueue: Create message queues (use region)
                - ConfigureS3EventNotification: Link S3 to SQS
                - EnableCloudTrail: Enable CloudTrail logging
                - EnableVpcFlowLogs: Enable VPC flow logging

                Example of how to call functions:
                Use the plugin functions directly in your responses.
                When creating OIDC provider, call CreateOidcProvider with the actual values.
                When creating IAM role, call CreateSentinelRole with the actual values.
                When creating S3 bucket, call CreateS3BucketForLogs with the actual values.
                When creating SQS queue, call CreateSqsQueue with the actual values.
                When enabling CloudTrail, call EnableCloudTrail with the actual values.

                IMPORTANT:
                - Actually INVOKE the functions, don't just describe what you would do
                - Use the plugin syntax {{PluginName.FunctionName}} with parameters to call functions
                - Return actual Role ARN and SQS URLs to coordinator for Azure configuration"
        };

        // 4. INTEGRATION AGENT - Connects AWS and Azure
        var integrationKernel = _kernel.Clone();
        _agents["integration"] = new ChatCompletionAgent
        {
            Name = "IntegrationAgent",
            Kernel = integrationKernel,
            Instructions = @"You are the integration specialist that connects AWS and Azure.
                Your responsibilities:
                1. Validate AWS resources are ready
                2. Ensure Azure Sentinel is prepared
                3. Map AWS log types to Sentinel tables
                4. Test end-to-end connectivity
                5. Troubleshoot connection issues

                Work with outputs from both AzureAgent and AwsAgent to:
                - Verify OIDC authentication works
                - Confirm SQS queues are accessible from Azure
                - Validate log format compatibility
                - Test data ingestion pipeline

                Report integration status and any issues to coordinator."
        };

        // 5. MONITOR AGENT - Validates and monitors the setup
        var monitorKernel = _kernel.Clone();
        _agents["monitor"] = new ChatCompletionAgent
        {
            Name = "MonitorAgent",
            Kernel = monitorKernel,
            Instructions = @"You are the monitoring and validation specialist.
                Your responsibilities:
                1. Verify all components are properly configured
                2. Check data flow from AWS to Sentinel
                3. Monitor ingestion rates and errors
                4. Validate security configurations
                5. Generate health reports

                After setup completion:
                - Confirm logs are appearing in Sentinel
                - Check for any errors or warnings
                - Validate data formats are correct
                - Monitor initial ingestion performance

                Report all findings to coordinator for final report."
        };

        _logger.LogInformation("Initialized {Count} specialized agents for Sentinel connector setup", _agents.Count);
    }

    /// <summary>
    /// Initialize the group chat with selection and termination strategies
    /// </summary>
    private void InitializeGroupChat()
    {
        // Create the group chat with all agents
        _groupChat = new AgentGroupChat(_agents.Values.ToArray())
        {
            ExecutionSettings = new AgentGroupChatSettings
            {
                // Use Semantic Kernel's built-in selection strategy
                // The agents will be selected based on their instructions and context
                SelectionStrategy = new SequentialSelectionStrategy(),

                // Use a simple termination strategy based on keywords
                TerminationStrategy = new RegexTerminationStrategy("SETUP COMPLETE|FINAL REPORT GENERATED|FATAL ERROR")
                {
                    MaximumIterations = 50
                }
            }
        };

        _logger.LogInformation("Initialized group chat with Semantic Kernel's built-in strategies");
    }

    /// <summary>
    /// Execute the Sentinel connector setup with a message and progress callback
    /// </summary>
    public async Task<string> ExecuteSetupAsync(
        string setupMessage,
        Action<string, string, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (_groupChat == null)
        {
            throw new InvalidOperationException("Group chat not initialized");
        }

        var responseBuilder = new System.Text.StringBuilder();
        var currentPhase = "validation";

        try
        {
            // Add initial message to chat
            _groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, setupMessage));

            // Execute the group chat
            await foreach (var message in _groupChat.InvokeAsync(cancellationToken))
            {
                var agentName = message.AuthorName ?? "Unknown";
                var content = message.Content ?? string.Empty;

                _logger.LogDebug("[{Agent}]: {Content}", agentName, content);

                // Determine the phase based on agent
                currentPhase = agentName switch
                {
                    "CoordinatorAgent" => "validation",
                    "AwsAgent" => "aws-setup",
                    "AzureAgent" => "azure-setup",
                    "IntegrationAgent" => "integration",
                    "MonitorAgent" => "monitoring",
                    _ => currentPhase
                };

                // Send progress updates via callback
                progressCallback?.Invoke(currentPhase, agentName, content);

                // Build the response
                responseBuilder.AppendLine($"[{agentName}]: {content}");

                // Check for completion
                if (content.Contains("SETUP COMPLETE", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("FINAL REPORT GENERATED", StringComparison.OrdinalIgnoreCase))
                {
                    progressCallback?.Invoke("completion", agentName, "Setup completed successfully");
                    break;
                }
            }

            return responseBuilder.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Sentinel connector setup");
            progressCallback?.Invoke("error", "System", $"Error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Execute the Sentinel connector setup through group chat
    /// </summary>
    public async Task<SetupResult> ExecuteSetupAsync(
        SetupConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (_groupChat == null)
        {
            throw new InvalidOperationException("Group chat not initialized");
        }

        var setupResult = new SetupResult();

        try
        {
            // Start the conversation with the coordinator
            var initialMessage = $@"
                Setup AWS-Azure Sentinel connector with the following configuration:
                - Workspace ID: {configuration.WorkspaceId}
                - Tenant ID: {configuration.TenantId}
                - Subscription ID: {configuration.SubscriptionId}
                - Resource Group: {configuration.ResourceGroupName}
                - Log Types: {string.Join(", ", configuration.LogTypes)}
                - AWS Region: {configuration.AwsRegion}

                Please coordinate the complete setup process.";

            // Add initial message to chat
            _groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, initialMessage));

            // Execute the group chat
            await foreach (var message in _groupChat.InvokeAsync(cancellationToken))
            {
                _logger.LogDebug("[{Agent}]: {Content}",
                    message.AuthorName ?? "Unknown",
                    message.Content);

                // Track progress
                setupResult.Messages.Add(new AgentMessage
                {
                    Agent = message.AuthorName ?? "Unknown",
                    Content = message.Content ?? string.Empty,
                    Timestamp = DateTime.UtcNow
                });

                // Check for specific outputs we need to capture
                if (message.AuthorName == "AwsAgent" && message.Content?.Contains("roleArn") == true)
                {
                    // Extract Role ARN from AWS agent response
                    setupResult.AwsRoleArn = ExtractValue(message.Content, "roleArn");
                }

                if (message.AuthorName == "AwsAgent" && message.Content?.Contains("queueUrl") == true)
                {
                    // Extract SQS URLs from AWS agent response
                    var sqsUrl = ExtractValue(message.Content, "queueUrl");
                    if (!string.IsNullOrEmpty(sqsUrl))
                    {
                        setupResult.SqsUrls.Add(sqsUrl);
                    }
                }

                if (message.AuthorName == "AzureAgent" && message.Content?.Contains("connectorName") == true)
                {
                    // Extract connector ID from Azure agent response
                    setupResult.ConnectorId = ExtractValue(message.Content, "connectorName");
                }
            }

            setupResult.Success = true;
            setupResult.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Sentinel connector setup");
            setupResult.Success = false;
            setupResult.ErrorMessage = ex.Message;
        }

        return setupResult;
    }

    private string ExtractValue(string content, string key)
    {
        try
        {
            // Try to parse as JSON first
            var json = JsonDocument.Parse(content);
            if (json.RootElement.TryGetProperty(key, out var value))
            {
                return value.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Fall back to simple string search
            var keyIndex = content.IndexOf($"\"{key}\":", StringComparison.OrdinalIgnoreCase);
            if (keyIndex >= 0)
            {
                var valueStart = content.IndexOf("\"", keyIndex + key.Length + 3) + 1;
                var valueEnd = content.IndexOf("\"", valueStart);
                if (valueStart > 0 && valueEnd > valueStart)
                {
                    return content.Substring(valueStart, valueEnd - valueStart);
                }
            }
        }
        return string.Empty;
    }
}


/// <summary>
/// Configuration for the Sentinel connector setup
/// </summary>
public class SetupConfiguration
{
    public string WorkspaceId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroupName { get; set; } = string.Empty;
    public List<string> LogTypes { get; set; } = new();
    public string AwsRegion { get; set; } = "us-east-1";
}

/// <summary>
/// Result of the Sentinel connector setup
/// </summary>
public class SetupResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AwsRoleArn { get; set; }
    public List<string> SqsUrls { get; set; } = new();
    public string? ConnectorId { get; set; }
    public DateTime CompletedAt { get; set; }
    public List<AgentMessage> Messages { get; set; } = new();
}

/// <summary>
/// Message from an agent during setup
/// </summary>
public class AgentMessage
{
    public string Agent { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}