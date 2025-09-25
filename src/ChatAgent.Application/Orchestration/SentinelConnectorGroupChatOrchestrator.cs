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
/// User-driven multi-agent system for AWS-Azure Sentinel connector setup.
/// Allows users to directly interact with and coordinate agents.
/// </summary>
public class SentinelConnectorGroupChatOrchestrator : IOrchestrator
{
    private readonly Kernel _kernel;
    private readonly ILogger<SentinelConnectorGroupChatOrchestrator> _logger;
    private readonly Dictionary<string, ChatCompletionAgent> _agents = new();
    private readonly IServiceProvider? _serviceProvider;
    private readonly IConversationRepository _conversationRepository;
    private readonly Dictionary<string, AgentGroupChat> _sessionGroupChats = new();
    private readonly Dictionary<string, string> _sessionCurrentAgent = new();

    public SentinelConnectorGroupChatOrchestrator(
        Kernel kernel,
        ILogger<SentinelConnectorGroupChatOrchestrator> logger,
        IConversationRepository conversationRepository,
        IServiceProvider? serviceProvider = null)
    {
        _kernel = kernel;
        _logger = logger;
        _conversationRepository = conversationRepository;
        _serviceProvider = serviceProvider;

        InitializeAgents();
    }

    /// <summary>
    /// Initialize specialized agents for the Sentinel connector setup
    /// </summary>
    private void InitializeAgents()
    {
        // COORDINATOR AGENT - The primary interface with users
        var coordinatorKernel = _kernel.Clone();
        var coordinatorPlugin = _serviceProvider?.GetService<CoordinatorPlugin>();

        if (coordinatorPlugin != null)
        {
            coordinatorKernel.Plugins.AddFromObject(coordinatorPlugin, "CoordinatorTools");
        }
        else
        {
            _logger.LogWarning("CoordinatorPlugin not available - CoordinatorAgent will have limited functionality");
        }

        _agents["coordinator"] = new ChatCompletionAgent
        {
            Name = "CoordinatorAgent",
            Kernel = coordinatorKernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.3,
                MaxTokens = 1200
            }),
            Instructions = @"You are the Sentinel Connector Setup Coordinator - the PRIMARY interface with users.

                YOUR ROLE:
                1. You are the ONLY agent who talks directly to users
                2. Coordinate with AzureAgent for all Azure/Sentinel operations
                3. You DO NOT have direct access to Azure functions - only AzureAgent does
                4. When users need Azure information, you MUST delegate to AzureAgent

                YOUR COORDINATOR TOOLS (you can call these directly):
                - ValidatePrerequisites(subscriptionId, tenantId, workspaceId): Check requirements
                - PlanConnectorSetup(logTypes, awsRegion): Create setup plan
                - GenerateSetupReport(setupDetails): Produce final report

                AZURE OPERATIONS (delegate to AzureAgent):
                - Finding connector solutions
                - Checking installation status
                - Any Azure resource queries

                CRITICAL WORKFLOW:
                1. User mentions AWS, connector, or solution: Say 'Let me find the AWS connector solution for you'
                2. User provides all required info: Validate and plan setup
                3. The selection strategy will automatically engage AzureAgent when needed
                4. Always relay AzureAgent's technical findings to user

                WHEN USER PROVIDES THESE DETAILS:
                - Azure subscription ID
                - Azure tenant ID
                - Resource group name
                - Workspace name
                - AWS region
                - Log type

                YOU MUST:
                1. Call ValidatePrerequisites with subscription, tenant, and workspace IDs
                2. Call PlanConnectorSetup with log type and AWS region
                3. Say 'Now I will find the AWS connector solution details for your workspace'
                4. When AzureAgent returns (in the next message), share the solution details:
                   - Solution ID
                   - Solution Name
                   - Version
                   - Installation status

                IMPORTANT:
                - Do NOT try to call 'AzureAgent' as a function - it is not a function
                - Just mention finding the solution, and the system will route to AzureAgent
                - When you mention 'find', 'solution', or 'azure' the system engages AzureAgent automatically

                NEVER:
                - Keep asking for info already provided
                - Try to find solutions yourself (only AzureAgent can)
                - Skip engaging AzureAgent when Azure info is needed

                Remember: You coordinate, AzureAgent executes Azure operations."
        };

        // AZURE AGENT - Handles Azure Sentinel operations
        var azureKernel = _kernel.Clone();
        var azurePlugin = _serviceProvider?.GetService<AzurePlugin>();

        if (azurePlugin != null)
        {
            azureKernel.Plugins.AddFromObject(azurePlugin, "AzureTools");
        }
        else
        {
            _logger.LogWarning("AzurePlugin not available - AzureAgent will have limited functionality");
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
            Instructions = @"You are an Azure Sentinel technical specialist. You execute Azure operations autonomously.

                YOUR AVAILABLE FUNCTION:
                FindConnectorSolution - Finds Azure Sentinel connector solutions

                AUTONOMOUS BEHAVIOR:
                When selected, immediately scan the ENTIRE conversation history for:
                - Azure subscription ID (look for patterns like 'cf697e34-75ce-497c-a639-0099c2f02bf4')
                - Resource group name (look for 'resource group' or 'resourceGroupName')
                - Workspace name (look for 'workspace name' or 'log analytics workspace name', NOT the ID)
                - Connector type (AWS, S3, CloudTrail all mean 'AWS')

                IMMEDIATE ACTION:
                If you find ALL four parameters in the conversation, immediately call:
                FindConnectorSolution with the parameters:
                - connectorName: 'AWS'
                - subscriptionId: [the subscription ID you found]
                - resourceGroupName: [the resource group name you found]
                - workspaceName: [the workspace name you found]

                EXAMPLE from conversation:
                'Azure subscription ID: cf697e34-75ce-497c-a639-0099c2f02bf4'
                'Resource group name: urelmattis'
                'Log analytics workspace name: test-workspace'

                You would immediately call FindConnectorSolution with those exact values.

                RESPONSE FORMAT:
                After calling the function, report back with:
                'Found AWS connector solution:
                - Solution ID: [returned value]
                - Solution Name: [returned value]
                - Version: [returned value]
                - Installation Status: [returned value]'

                NEVER ask for information - extract it from conversation history.
                ALWAYS act immediately when selected."
        };




        _logger.LogInformation("Initialized {Count} specialized agents for Sentinel connector setup", _agents.Count);
    }

    /// <summary>
    /// Process a message from the user in a conversational manner
    /// </summary>
    public async Task<Domain.Entities.ChatMessage> ProcessMessageAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing user message in session {SessionId}: {Message}", sessionId, message);

        var conversation = await GetOrCreateConversationAsync(sessionId, cancellationToken);
        var userMessage = new Domain.Entities.ChatMessage(message, "user");
        conversation.AddMessage(userMessage);

        // Process the message conversationally
        var response = await ProcessConversationalMessageAsync(message, sessionId, conversation, cancellationToken);

        var assistantMessage = new Domain.Entities.ChatMessage(
            response.Content,
            "assistant",
            response.AgentId);

        conversation.AddMessage(assistantMessage);
        await _conversationRepository.UpdateAsync(conversation, cancellationToken);

        return assistantMessage;
    }

    private async Task<(string Content, string AgentId)> ProcessConversationalMessageAsync(
        string message,
        string sessionId,
        Domain.Entities.Conversation conversation,
        CancellationToken cancellationToken)
    {
        // Analyze the message to understand user intent
        var intent = AnalyzeUserIntent(message, conversation);

        switch (intent.Type)
        {
            case IntentType.AskForHelp:
                return await GetConversationalHelpAsync(cancellationToken);

            case IntentType.CheckStatus:
                return await GetConversationalStatusAsync(sessionId, cancellationToken);

            case IntentType.StartDeployment:
                return await StartConversationalDeploymentAsync(message, sessionId, cancellationToken);

            case IntentType.AWSConfiguration:
                return await HandleAWSConversationAsync(message, sessionId, cancellationToken);

            case IntentType.AzureConfiguration:
                return await HandleAzureConversationAsync(message, sessionId, cancellationToken);

            case IntentType.Validation:
                return await HandleValidationConversationAsync(message, sessionId, cancellationToken);

            case IntentType.Troubleshooting:
                return await HandleTroubleshootingAsync(message, sessionId, cancellationToken);

            case IntentType.GeneralQuestion:
            default:
                return await HandleGeneralConversationAsync(message, sessionId, conversation, cancellationToken);
        }
    }

    private UserIntent AnalyzeUserIntent(string message, Domain.Entities.Conversation conversation)
    {
        var lowerMessage = message.ToLower();

        // Check for help requests
        if (lowerMessage.Contains("help") || lowerMessage.Contains("how do i") ||
            lowerMessage.Contains("what can") || lowerMessage.Contains("guide me"))
        {
            return new UserIntent { Type = IntentType.AskForHelp };
        }

        // Check for status inquiries
        if (lowerMessage.Contains("status") || lowerMessage.Contains("progress") ||
            lowerMessage.Contains("where are we") || lowerMessage.Contains("what's done"))
        {
            return new UserIntent { Type = IntentType.CheckStatus };
        }

        // Check for deployment initiation
        if (lowerMessage.Contains("start") || lowerMessage.Contains("begin") ||
            lowerMessage.Contains("deploy") || lowerMessage.Contains("set up") ||
            lowerMessage.Contains("configure sentinel connector"))
        {
            return new UserIntent { Type = IntentType.StartDeployment };
        }

        // Check for AWS-related topics
        if (lowerMessage.Contains("aws") || lowerMessage.Contains("s3") ||
            lowerMessage.Contains("iam") || lowerMessage.Contains("sqs") ||
            lowerMessage.Contains("cloudtrail"))
        {
            return new UserIntent { Type = IntentType.AWSConfiguration };
        }

        // Check for Azure/Sentinel topics
        if (lowerMessage.Contains("azure") || lowerMessage.Contains("sentinel") ||
            lowerMessage.Contains("workspace") || lowerMessage.Contains("data connector"))
        {
            return new UserIntent { Type = IntentType.AzureConfiguration };
        }

        // Check for validation requests
        if (lowerMessage.Contains("validate") || lowerMessage.Contains("verify") ||
            lowerMessage.Contains("check") || lowerMessage.Contains("test"))
        {
            return new UserIntent { Type = IntentType.Validation };
        }

        // Check for troubleshooting
        if (lowerMessage.Contains("error") || lowerMessage.Contains("problem") ||
            lowerMessage.Contains("issue") || lowerMessage.Contains("not working") ||
            lowerMessage.Contains("failed"))
        {
            return new UserIntent { Type = IntentType.Troubleshooting };
        }

        return new UserIntent { Type = IntentType.GeneralQuestion };
    }

    private Task<(string Content, string AgentId)> GetConversationalHelpAsync(
        CancellationToken cancellationToken)
    {
        var helpText = @"I'm here to help you set up the AWS-Azure Sentinel connector! Here's what we can do together:

🚀 **Getting Started**: Just tell me you want to 'start setting up the Sentinel connector' or 'begin deployment'

💬 **Natural Conversation**: Talk to me naturally about what you need:
   • 'I need to set up AWS resources for Sentinel'
   • 'Help me configure the Azure side'
   • 'Let's validate our setup'
   • 'Can you check if everything is working?'

📊 **Check Progress**: Ask me 'what's our status?' or 'how are we doing?'

🔧 **Troubleshooting**: Tell me about any issues: 'I'm getting an error' or 'something's not working'

👥 **Collaborative Work**: I can coordinate multiple specialists:
   • AWS Infrastructure Expert
   • Azure Sentinel Specialist
   • Integration Expert
   • Monitoring Specialist

Just describe what you want to accomplish, and I'll engage the right experts to help!";

        return Task.FromResult((helpText, "assistant"));
    }

    private Task<(string Content, string AgentId)> GetConversationalStatusAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        // Get actual status from deployment tracking
        var status = GetCurrentDeploymentPhase(sessionId);

        var statusMessage = $@"Let me check where we are in the deployment process...

📍 **Current Status**: {status.Phase}

✅ **Completed Steps**:
{string.Join("\n", status.CompletedSteps.Select(s => $"   • {s}"))}

⏳ **Next Steps**:
{string.Join("\n", status.PendingSteps.Take(3).Select(s => $"   • {s}"))}

{GetNextActionSuggestion(status)}

Would you like to continue with the next step?";

        return Task.FromResult((statusMessage, "coordinator"));
    }

    private async Task<(string Content, string AgentId)> StartConversationalDeploymentAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // Use the coordinator-controlled orchestration for deployment
        return await CoordinatorControlledOrchestrationAsync(
            "The user wants to start setting up the AWS-Azure Sentinel connector. Begin the deployment process by validating prerequisites and creating a setup plan.",
            sessionId,
            cancellationToken);
    }

    private async Task<(string Content, string AgentId)> HandleAWSConversationAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // Route AWS-related queries through the coordinator
        return await CoordinatorControlledOrchestrationAsync(message, sessionId, cancellationToken);
    }

    private async Task<(string Content, string AgentId)> HandleAzureConversationAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // Route Azure-related queries through the coordinator
        return await CoordinatorControlledOrchestrationAsync(message, sessionId, cancellationToken);
    }

    private async Task<(string Content, string AgentId)> HandleValidationConversationAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // Route validation requests through the coordinator
        return await CoordinatorControlledOrchestrationAsync(message, sessionId, cancellationToken);
    }

    private async Task<(string Content, string AgentId)> HandleTroubleshootingAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken)
    {
        // Route troubleshooting through the coordinator
        return await CoordinatorControlledOrchestrationAsync(message, sessionId, cancellationToken);
    }

    private async Task<(string Content, string AgentId)> HandleGeneralConversationAsync(
        string message,
        string sessionId,
        Domain.Entities.Conversation conversation,
        CancellationToken cancellationToken)
    {
        // Use coordinator-controlled orchestration instead of free-form group chat
        // The coordinator will manage all specialist interactions internally
        return await CoordinatorControlledOrchestrationAsync(message, sessionId, cancellationToken);
    }

    /// <summary>
    /// Implements a coordinator-controlled orchestration pattern where:
    /// 1. Only the coordinator communicates with the user
    /// 2. All agents share the same conversation history via group chat
    /// 3. Agents can see all previous messages and context
    /// </summary>
    private async Task<(string Content, string AgentId)> CoordinatorControlledOrchestrationAsync(
        string userMessage,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!_agents.TryGetValue("coordinator", out var coordinator))
        {
            return ("I'm unable to access the coordinator agent. Please try again.", "system");
        }

        try
        {
            // Get or create the group chat for this session with shared history
            // Check if chat exists and if it's been completed (terminated)
            if (_sessionGroupChats.TryGetValue(sessionId, out var existingChat))
            {
                // Check if the chat has been completed/terminated
                // If it has, we need to create a new one
                bool isCompleted = false;
                try
                {
                    // Try to check if we can still use the chat
                    // The IsComplete property or similar would be ideal, but we'll handle the exception
                    isCompleted = existingChat.IsComplete;
                }
                catch
                {
                    // If we can't check, assume it might be completed
                    isCompleted = true;
                }

                if (isCompleted)
                {
                    // Remove the completed chat and create a new one
                    _sessionGroupChats.Remove(sessionId);
                    _logger.LogDebug("Previous chat was completed, creating new chat for session {SessionId}", sessionId);
                }
            }

            if (!_sessionGroupChats.TryGetValue(sessionId, out var groupChat))
            {
                // Create a new group chat with coordinator and azure agents
                var agentsForChat = new List<ChatCompletionAgent> { coordinator };

                if (_agents.TryGetValue("azure", out var azureAgent))
                {
                    agentsForChat.Add(azureAgent);
                }

                groupChat = new AgentGroupChat(agentsForChat.ToArray())
                {
                    ExecutionSettings = new AgentGroupChatSettings
                    {
                        TerminationStrategy = new CoordinatorTerminationStrategy(),
                        SelectionStrategy = new CoordinatorSelectionStrategy(_logger)
                    }
                };

                _sessionGroupChats[sessionId] = groupChat;

                // Load existing conversation history so agents see all context
                var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
                if (conversation != null && conversation.Messages.Any())
                {
                    foreach (var historicalMessage in conversation.Messages)
                    {
                        var role = historicalMessage.Role == "user" ? AuthorRole.User : AuthorRole.Assistant;
                        var authorName = historicalMessage.Role == "user" ? "User" : historicalMessage.AgentId ?? "Coordinator";

                        // Add all historical messages to group chat so agents have full context
                        groupChat.AddChatMessage(new ChatMessageContent(role, historicalMessage.Content)
                        {
                            AuthorName = authorName
                        });
                    }
                }
            }

            // Add the new user message to the shared history
            groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, userMessage)
            {
                AuthorName = "User"
            });

            // Process through group chat - all agents see the full conversation history
            var coordinatorResponses = new List<string>();

            try
            {
                await foreach (var response in groupChat.InvokeAsync(cancellationToken))
                {
                    if (response.AuthorName == "CoordinatorAgent" && !string.IsNullOrEmpty(response.Content))
                    {
                        coordinatorResponses.Add(response.Content);
                    }

                    _logger.LogDebug("[{Agent}]: {Content}", response.AuthorName, response.Content);
                }
            }
            catch (KernelException kex) when (kex.Message.Contains("Chat has completed"))
            {
                // The chat was already completed, remove it and retry with a new chat
                _logger.LogWarning("Chat was already completed for session {SessionId}, creating new chat", sessionId);
                _sessionGroupChats.Remove(sessionId);

                // Recursively call ourselves to create a new chat and process the message
                return await CoordinatorControlledOrchestrationAsync(userMessage, sessionId, cancellationToken);
            }

            var finalResponse = coordinatorResponses.Any()
                ? string.Join("\n", coordinatorResponses)
                : "I'm processing your request. Please wait a moment.";

            return (finalResponse, "coordinator");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in coordinator-controlled orchestration");
            return ("I encountered an issue coordinating the response. Let me try a different approach.", "coordinator");
        }
    }

    private async Task<(string Content, string AgentId)> SendToAgentAsync(
        ChatCompletionAgent agent,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var agentMessage = new ChatMessageContent(AuthorRole.User, message);
            var response = new List<string>();

            await foreach (ChatMessageContent item in agent.InvokeAsync(agentMessage, cancellationToken: cancellationToken))
            {
                if (item?.Content != null)
                {
                    response.Add(item.Content);
                }
                if (response.Count > 0) break; // Take first response
            }

            var content = response.Count > 0 ? string.Join("\n", response) : "No response from agent";
            return (content, agent.Name ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking agent {AgentName}", agent.Name);
            return ($"Error: {ex.Message}", "system");
        }
    }

    private enum IntentType
    {
        AskForHelp,
        CheckStatus,
        StartDeployment,
        AWSConfiguration,
        AzureConfiguration,
        Validation,
        Troubleshooting,
        GeneralQuestion
    }

    private class UserIntent
    {
        public IntentType Type { get; set; }
    }

    private class DeploymentPhase
    {
        public string Phase { get; set; } = "Not Started";
        public List<string> CompletedSteps { get; set; } = new();
        public List<string> PendingSteps { get; set; } = new();
    }

    private DeploymentPhase GetCurrentDeploymentPhase(string sessionId)
    {
        // In production, this would retrieve actual status from storage
        return new DeploymentPhase
        {
            Phase = "Ready to Start",
            CompletedSteps = new List<string>(),
            PendingSteps = new List<string>
            {
                "Validate prerequisites",
                "Create AWS resources",
                "Configure Azure Sentinel",
                "Verify data flow"
            }
        };
    }

    private string GetNextActionSuggestion(DeploymentPhase status)
    {
        if (status.PendingSteps.Count == 0)
            return "🎉 Great job! Your deployment is complete!";

        var nextStep = status.PendingSteps.FirstOrDefault();
        return nextStep switch
        {
            "Validate prerequisites" => "💡 Ready to validate? Just say 'let's validate the prerequisites' to begin.",
            "Create AWS resources" => "💡 Shall we set up the AWS side? Say 'let's create the AWS resources' to continue.",
            "Configure Azure Sentinel" => "💡 Time for Azure! Say 'configure Sentinel' to proceed.",
            "Verify data flow" => "💡 Final step! Say 'verify everything is working' to test.",
            _ => "💡 Ready for the next step when you are!"
        };
    }

    private async Task<Domain.Entities.Conversation> GetOrCreateConversationAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
        if (conversation == null)
        {
            conversation = new Domain.Entities.Conversation(sessionId);
            await _conversationRepository.CreateAsync(conversation, cancellationToken);
        }
        return conversation;
    }

    public async Task<Domain.Entities.Conversation> GetConversationAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
        return conversation ?? new Domain.Entities.Conversation(sessionId);
    }

    public Task<List<Domain.Entities.Agent>> GetAvailableAgentsAsync(
        CancellationToken cancellationToken = default)
    {
        var agents = new List<Domain.Entities.Agent>();

        foreach (var (key, agent) in _agents)
        {
            var domainAgent = new Domain.Entities.Agent(
                key,
                agent.Name ?? key,
                agent.Instructions ?? string.Empty,
                ChatAgent.Domain.Entities.AgentType.Tool);

            // Add capabilities based on agent type
            switch (key)
            {
                case "coordinator":
                    domainAgent.AddCapability("validation");
                    domainAgent.AddCapability("planning");
                    domainAgent.AddCapability("reporting");
                    break;
                case "aws":
                    domainAgent.AddCapability("oidc-setup");
                    domainAgent.AddCapability("iam-roles");
                    domainAgent.AddCapability("s3-buckets");
                    domainAgent.AddCapability("sqs-queues");
                    break;
                case "azure":
                    domainAgent.AddCapability("sentinel-config");
                    domainAgent.AddCapability("data-connectors");
                    domainAgent.AddCapability("arm-deployment");
                    break;
            }

            agents.Add(domainAgent);
        }

        return Task.FromResult(agents);
    }

    public Task RegisterAgentAsync(
        Domain.Entities.Agent agent,
        CancellationToken cancellationToken = default)
    {
        // Not implementing dynamic agent registration for this specialized orchestrator
        throw new NotImplementedException("Dynamic agent registration not supported");
    }

    /// <summary>
    /// Execute the Sentinel connector setup with a message and progress callback
    /// </summary>
    public async Task<string> ExecuteSetupAsync(
        string setupMessage,
        Action<string, string, string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Create a new group chat for this setup execution
        var groupChat = new AgentGroupChat(_agents.Values.ToArray())
        {
            ExecutionSettings = new AgentGroupChatSettings
            {
                SelectionStrategy = new SentinelSetupSelectionStrategy(_logger),
                TerminationStrategy = new RegexTerminationStrategy("SETUP COMPLETE|FINAL REPORT GENERATED")
                {
                    MaximumIterations = 10
                }
            }
        };

        var responseBuilder = new System.Text.StringBuilder();
        var currentPhase = "validation";

        try
        {
            // Add initial message to chat
            groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, setupMessage));

            // Execute the group chat
            await foreach (var message in groupChat.InvokeAsync(cancellationToken))
            {
                var agentName = message.AuthorName ?? "Unknown";
                var content = message.Content ?? string.Empty;

                _logger.LogDebug("[{Agent}]: {Content}", agentName, content);

                // Determine the phase based on agent
                currentPhase = agentName switch
                {
                    "CoordinatorAgent" => "validation",
                    "AzureAgent" => "azure-setup",
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
        // Create a new group chat for this setup execution
        var groupChat = new AgentGroupChat(_agents.Values.ToArray())
        {
            ExecutionSettings = new AgentGroupChatSettings
            {
                SelectionStrategy = new SentinelSetupSelectionStrategy(_logger),
                TerminationStrategy = new RegexTerminationStrategy("SETUP COMPLETE|FINAL REPORT GENERATED")
                {
                    MaximumIterations = 10
                }
            }
        };

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
            groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, initialMessage));

            // Execute the group chat
            await foreach (var message in groupChat.InvokeAsync(cancellationToken))
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

                // Check for specific outputs we need to capture - from any agent
                if (message.Content?.Contains("roleArn") == true)
                {
                    // Extract Role ARN from agent response
                    setupResult.AwsRoleArn = ExtractValue(message.Content, "roleArn");
                }

                if (message.Content?.Contains("queueUrl") == true)
                {
                    // Extract SQS URLs from agent response
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