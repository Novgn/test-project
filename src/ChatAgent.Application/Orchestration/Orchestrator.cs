using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
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
public class Orchestrator : IOrchestrator
{
    private readonly Kernel _kernel;
    private readonly ILogger<Orchestrator> _logger;
    private readonly Dictionary<string, ChatCompletionAgent> _agents = new();
    private readonly IServiceProvider? _serviceProvider;
    private readonly IConversationRepository _conversationRepository;
    private readonly Dictionary<string, ChatHistory> _sessionHistories = new();

    public Orchestrator(
        Kernel kernel,
        ILogger<Orchestrator> logger,
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
        _logger.LogInformation("Starting agent initialization...");

        // Check the base kernel first
        var baseChatService = _kernel.Services.GetService<IChatCompletionService>();
        if (baseChatService == null)
        {
            _logger.LogError("CRITICAL: No IChatCompletionService in base kernel!");
        }
        else
        {
            _logger.LogInformation("Base kernel has chat service: {ServiceType}", baseChatService.GetType().Name);
        }

        // COORDINATOR AGENT - Use the SAME kernel for all agents (following SimpleGroupChatOrchestrator pattern)
        // This is critical for the agents to work properly with the runtime

        // Verify the shared kernel has chat completion service
        var chatService = _kernel.Services.GetService<IChatCompletionService>();
        if (chatService == null)
        {
            _logger.LogError("No IChatCompletionService found in kernel for CoordinatorAgent!");
        }
        else
        {
            _logger.LogInformation("Shared kernel has chat service: {ServiceType}", chatService.GetType().Name);
        }

        // Add coordinator plugin for enhanced functionality
        var coordinatorPlugin = _serviceProvider?.GetService<CoordinatorPlugin>();

        if (coordinatorPlugin != null)
        {
            _kernel.Plugins.AddFromObject(coordinatorPlugin, "CoordinatorTools");
            _logger.LogInformation("CoordinatorPlugin added successfully");
        }
        else
        {
            _logger.LogWarning("CoordinatorPlugin not available - CoordinatorAgent will have limited functionality");
        }

        // Enhanced coordinator agent with clear instructions
        var coordinatorAgent = new ChatCompletionAgent
        {
            Name = "CoordinatorAgent",
            Description = "The main coordinator for Sentinel connector setup",
            Instructions = @"You are the Coordinator for setting up AWS-Azure Sentinel connectors.

                YOUR PRIMARY ROLE:
                - Guide users through the Sentinel connector setup process
                - Provide clear, step-by-step instructions
                - Answer questions about both AWS and Azure aspects
                - Help troubleshoot any issues

                AVAILABLE TOOLS:
                - ValidatePrerequisites: Check if all requirements are met
                - PlanConnectorSetup: Create a detailed setup plan
                - GenerateSetupReport: Provide a comprehensive report

                COMMUNICATION STYLE:
                - Be concise but informative
                - Use bullet points for clarity
                - Focus on actionable next steps
                - Keep responses under 200 words unless more detail is requested",
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.3,
                MaxTokens = 800
            })
        };

        _agents["coordinator"] = coordinatorAgent;

        // AZURE AGENT - Add Azure plugin for enhanced functionality
        var azurePlugin = _serviceProvider?.GetService<AzurePlugin>();

        if (azurePlugin != null)
        {
            _kernel.Plugins.AddFromObject(azurePlugin, "AzureTools");
            _logger.LogInformation("AzurePlugin added successfully");
        }
        else
        {
            _logger.LogWarning("AzurePlugin not available - AzureAgent will have limited functionality");
        }

        // Enhanced Azure agent with specific responsibilities
        var azureAgent = new ChatCompletionAgent
        {
            Name = "AzureAgent",
            Description = "Azure Sentinel technical specialist",
            Instructions = @"You are an Azure Sentinel technical specialist.

                YOUR EXPERTISE:
                - Azure Sentinel workspace configuration
                - Data connector installation and management
                - Log Analytics workspace setup
                - Azure Resource Manager operations
                - Security solution deployment

                AVAILABLE TOOLS:
                - FindConnectorSolution: Search for available Sentinel connectors

                RESPONSE STYLE:
                - Provide Azure-specific technical details when relevant
                - Focus on Sentinel and Log Analytics aspects
                - Keep responses concise and actionable",
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2,
                MaxTokens = 600
            })
        };

        _agents["azure"] = azureAgent;

        // Enhanced AWS agent with specific expertise
        var awsAgent = new ChatCompletionAgent
        {
            Name = "AWSAgent",
            Description = "AWS infrastructure specialist",
            Instructions = @"You are an AWS infrastructure specialist for Sentinel integration.

                YOUR EXPERTISE:
                - AWS CloudTrail configuration for audit logging
                - S3 bucket setup and policies for log storage
                - IAM roles and cross-account permissions
                - SNS/SQS configuration for event streaming
                - AWS Security Hub integration

                RESPONSE STYLE:
                - Focus on AWS-side requirements and configuration
                - Provide specific IAM policy examples when needed
                - Help with S3 bucket policies and CloudTrail setup
                - Keep responses technical but clear",
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                Temperature = 0.3,
                MaxTokens = 600
            })
        };

        _agents["aws"] = awsAgent;

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
        // Always use the orchestration to get actual agent responses
        // No more hardcoded responses or quick commands
        return await CoordinatorControlledOrchestrationAsync(message, sessionId, cancellationToken);
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
        var helpText = "Hello! How can I help you with the Sentinel connector setup today?";

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
    /// 2. The coordinator manages all interactions with specialized agents
    /// 3. User drives the conversation through the coordinator
    /// 4. Fresh orchestration is created for each message per Semantic Kernel best practices
    /// </summary>
    private async Task<(string Content, string AgentId)> CoordinatorControlledOrchestrationAsync(
        string userMessage,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Create a fresh runtime for each message (following SimpleGroupChatOrchestrator pattern)
            var runtime = new InProcessRuntime();
            await runtime.StartAsync(cancellationToken);
            _logger.LogDebug("Runtime started for session {SessionId}", sessionId);

            // Get or create chat history for this session
            if (!_sessionHistories.TryGetValue(sessionId, out var chatHistory))
            {
                chatHistory = new ChatHistory();
                _sessionHistories[sessionId] = chatHistory;
            }

            // Always create a fresh orchestration for each message (best practice)
            _logger.LogDebug("Creating fresh orchestration for session {SessionId} with {HistoryCount} messages in history",
                sessionId, chatHistory.Count);

            // Use the persistent agents from initialization
            var members = new List<ChatCompletionAgent>();

            if (!_agents.TryGetValue("coordinator", out var coordinator))
            {
                _logger.LogError("Coordinator agent not found!");
                return ("Unable to access the coordinator agent.", "system");
            }
            members.Add(coordinator);

            if (_agents.TryGetValue("azure", out var azureAgent))
            {
                members.Add(azureAgent);
            }

            if (_agents.TryGetValue("aws", out var awsAgent))
            {
                members.Add(awsAgent);
            }

            // Log agent names for debugging
            _logger.LogDebug("Orchestration members: {Members}",
                string.Join(", ", members.Select(m => $"'{m.Name}' (Id: {m.Id})")));

            // Use the built-in RoundRobinGroupChatManager which we know works
            // Set MaximumInvocationCount to 1 so only the first agent (coordinator) responds
            var manager = new RoundRobinGroupChatManager
            {
                MaximumInvocationCount = 1 // Only allow coordinator to respond to keep responses focused
            };

            // Build conversation context for agents
            var contextPrompt = new System.Text.StringBuilder();
            if (chatHistory.Count > 0)
            {
                contextPrompt.AppendLine("Previous conversation context:");
                var contextMessages = chatHistory.TakeLast(10);
                foreach (var msg in contextMessages)
                {
                    var role = msg.Role == AuthorRole.User ? "User" :
                              (msg.AuthorName ?? "Assistant");
                    contextPrompt.AppendLine($"{role}: {msg.Content}");
                }
                contextPrompt.AppendLine("\nCurrent user message:");
            }
            contextPrompt.Append(userMessage);

            // Create the orchestration fresh for this interaction (matching SimpleGroupChatOrchestrator pattern)
            var orchestration = new GroupChatOrchestration(manager, members.ToArray());

            // Invoke orchestration with the context-enriched message
            _logger.LogInformation("Invoking fresh orchestration for session {SessionId} with context",
                sessionId);

            // Start the orchestration with the message (includes context)
            _logger.LogDebug("Invoking orchestration for session {SessionId}", sessionId);
            var result = await orchestration.InvokeAsync(contextPrompt.ToString(), runtime, cancellationToken);
            _logger.LogDebug("Orchestration invoked, result type: {ResultType}", result.GetType().Name);

            // Get the result with a timeout (following the working pattern from SimpleGroupChatOrchestrator)
            _logger.LogDebug("Getting orchestration result...");
            var finalResponse = await result.GetValueAsync(TimeSpan.FromSeconds(30));
            _logger.LogDebug("Got orchestration result: {ResponseLength} characters", finalResponse?.Length ?? 0);

            // Clean up the runtime
            _logger.LogDebug("Running runtime until idle for session {SessionId}", sessionId);
            await runtime.RunUntilIdleAsync();
            _logger.LogDebug("Runtime processing complete for session {SessionId}", sessionId);

            // Add the user message and response to the persistent history
            if (!string.IsNullOrEmpty(finalResponse))
            {
                chatHistory.AddUserMessage(userMessage);
                chatHistory.AddAssistantMessage(finalResponse);
                _logger.LogDebug("Updated session history, now contains {Count} messages", chatHistory.Count);
            }

            return (finalResponse ?? "I'm processing your request. Please wait a moment.", "coordinator");
        }
        catch (TimeoutException tex)
        {
            _logger.LogError(tex, "Orchestration timed out for session {SessionId}", sessionId);

            // Add diagnostic information
            _logger.LogDebug("Session {SessionId} had {HistoryCount} messages in history",
                sessionId, _sessionHistories.TryGetValue(sessionId, out var h) ? h.Count : 0);

            return ("The operation took too long to complete. Please try again with a simpler request.", "coordinator");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in coordinator-controlled orchestration for session {SessionId}", sessionId);
            return ("I encountered an issue coordinating the response. Please try again.", "coordinator");
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
}



