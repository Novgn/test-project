using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using ChatAgent.Application.Plugins.Azure;
using ChatAgent.Application.Plugins.Coordinator;
using ChatAgent.Application.Plugins.AWS;

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

        // Add coordinator plugin for enhanced functionality (check if already exists)
        if (!_kernel.Plugins.Contains("CoordinatorTools"))
        {
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
        }
        else
        {
            _logger.LogDebug("CoordinatorTools plugin already registered");
        }

        // Enhanced coordinator agent with clear instructions
        var coordinatorAgent = new ChatCompletionAgent
        {
            Name = "CoordinatorAgent",
            Description = "The main coordinator for Sentinel connector setup",
            Instructions = @"You are the Coordinator for setting up AWS-Azure Sentinel connectors.

                YOUR PRIMARY ROLE:
                - Guide users through the complete AWS to Microsoft Sentinel setup process
                - Ensure steps are executed in the correct order
                - Coordinate between AWS and Azure configurations
                - Help users make informed decisions about options

                AVAILABLE TOOLS:
                - ValidatePrerequisites: Check if all requirements are met
                - PlanConnectorSetup: Create a detailed setup plan
                - GenerateSetupReport: Provide a comprehensive report

                CORRECT SETUP ORDER (CRITICAL):
                1. Azure Side:
                   - Use Azure agent to find AWS connector solutions
                   - CHECK what's already installed in the workspace
                   - Present all options to user (installed and available)
                   - Get user confirmation before installing anything
                   - Install selected AWS S3 connector from Content Hub
                   - Get Workspace ID (this is the External ID)

                2. AWS Side (in this exact order):
                   a. Authentication Setup (SetupAWSAuth):
                      - Create OIDC provider for Microsoft Sentinel
                      - Create IAM role with web identity federation
                      - Attach necessary policies

                   b. Infrastructure Setup (SetupAWSInfra):
                      - Create S3 bucket with versioning and encryption
                      - Create SQS queue for notifications
                      - Configure bucket policies (includes role access)
                      - Configure SQS policies
                      - Set up S3 event notifications
                      - Optionally create CloudTrail (ask user about options)

                3. Final Configuration:
                   - Provide Role ARN and SQS URL to user
                   - Guide them to complete Azure portal configuration
                   - Help verify data ingestion

                KEY DECISIONS TO ASK USER:
                - Enable CloudTrail? (usually yes for audit logs)
                - Enable KMS encryption for CloudTrail? (more secure but adds complexity)
                - Multi-region trail? (recommended for complete visibility)
                - Organization-wide trail? (if managing multiple AWS accounts)
                - Enable S3 data events? (for detailed S3 access logging)

                COMMUNICATION STYLE:
                - Be explicit about the order of operations
                - Explain why each step matters
                - Ask about options before proceeding
                - Provide clear next steps",
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.3,
                MaxTokens = 800
            })
        };

        _agents["coordinator"] = coordinatorAgent;

        // AZURE AGENT - Add Azure plugin for enhanced functionality (check if already exists)
        if (!_kernel.Plugins.Contains("AzureTools"))
        {
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
        }
        else
        {
            _logger.LogDebug("AzureTools plugin already registered");
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
                - FindConnectorSolution: Search for available Sentinel connectors (also shows installed status)
                - InstallConnectorSolution: Install solutions from Content Hub into workspaces

                CRITICAL INSTALLATION PROCESS:
                1. ALWAYS use FindConnectorSolution first to get available options
                2. PRESENT the full list of solutions found, clearly indicating:
                   - Which solutions are ALREADY INSTALLED (mark with ✅)
                   - Which solutions are AVAILABLE but not installed (mark with ⬜)
                   - Solution name, description, publisher, and version
                3. If solutions are already installed, INFORM the user first
                4. ASK the user if they want to install additional solutions
                5. WAIT for user selection and confirmation
                6. NEVER automatically install without explicit user approval

                EXAMPLE INTERACTION:
                User: 'I need to set up AWS connector'
                You: 'Let me search for available AWS connectors in your workspace...'
                [Use FindConnectorSolution]
                You: 'Here's what I found for AWS solutions in your Sentinel workspace:

                **Already Installed:**
                ✅ 1. Amazon Web Services - by Microsoft (version 3.0.1)
                   Description: Ingest AWS service logs including CloudTrail, GuardDuty, etc.
                   Status: INSTALLED and ACTIVE

                **Available for Installation:**
                ⬜ 2. AWS Security Hub - by Microsoft (version 2.1.0)
                   Description: Integrate Security Hub findings into Sentinel

                ⬜ 3. AWS GuardDuty - by Microsoft (version 1.5.2)
                   Description: Ingest GuardDuty threat intelligence alerts

                You already have the main AWS connector installed. Would you like to install any additional AWS solutions?'

                User: 'Install Security Hub'
                You: 'I'll install the AWS Security Hub solution (version 2.1.0). This will add Security Hub integration capabilities. Please confirm you want to proceed.'
                User: 'Yes, proceed'
                [Use InstallConnectorSolution]

                IMPORTANT NOTES:
                - Always check and report installed solutions first
                - Help users avoid duplicate installations
                - Explain what each solution adds if not already installed
                - Be clear about versions and publishers
                - If the main solution is installed, suggest complementary ones

                RESPONSE STYLE:
                - Be interactive and informative
                - Use clear visual indicators (✅ for installed, ⬜ for available)
                - Present options with numbers for easy reference
                - Always show installation status upfront
                - Wait for explicit approval before any installation",
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
                Temperature = 0.2,
                MaxTokens = 600
            })
        };

        _agents["azure"] = azureAgent;

        // AWS AGENT - Add AWS plugin for enhanced functionality (check if already exists)
        if (!_kernel.Plugins.Contains("AWSTools"))
        {
            var awsPlugin = _serviceProvider?.GetService<AWSPlugin>();
            if (awsPlugin != null)
            {
                _kernel.Plugins.AddFromObject(awsPlugin, "AWSTools");
                _logger.LogInformation("AWSPlugin added successfully");
            }
            else
            {
                _logger.LogWarning("AWSPlugin not available - AWSAgent will have limited functionality");
            }
        }
        else
        {
            _logger.LogDebug("AWSTools plugin already registered");
        }

        // Enhanced AWS agent with specific expertise
        var awsAgent = new ChatCompletionAgent
        {
            Name = "AWSAgent",
            Description = "AWS infrastructure specialist",
            Instructions = @"You are an AWS infrastructure specialist for Sentinel integration.

                YOUR EXPERTISE:
                - OIDC provider configuration for Microsoft Sentinel
                - IAM roles with web identity federation
                - AWS CloudTrail configuration with all options
                - S3 bucket setup with proper policies
                - SQS configuration for real-time notifications
                - KMS encryption setup
                - Organization and multi-region trail configuration

                AVAILABLE TOOLS:
                - SetupAWSAuth: MUST BE RUN FIRST - Creates OIDC provider and IAM role
                - SetupAWSInfra: MUST BE RUN SECOND - Creates S3, SQS, and optionally CloudTrail
                - GenerateAWSSetupSummary: Provides summary after setup

                CRITICAL SETUP ORDER:
                1. ALWAYS run SetupAWSAuth first to create:
                   - OIDC identity provider (if not exists)
                   - IAM role with proper trust relationship
                   - Attach S3, SQS, and CloudTrail policies

                2. THEN run SetupAWSInfra to create:
                   - S3 bucket with versioning and optional encryption
                   - SQS queue with proper settings
                   - Bucket policy that grants role AND CloudTrail access
                   - S3 event notifications to SQS
                   - CloudTrail (if requested) with chosen options

                CLOUDTRAIL OPTIONS TO DISCUSS:
                - Enable CloudTrail: Usually yes for audit logs
                - KMS encryption: Adds security but requires KMS key management
                - Multi-region: Yes for complete AWS visibility
                - Organization trail: Yes if multiple AWS accounts
                - Data events: Yes for detailed S3 access logs (can be costly)
                - Log file validation: Yes for tamper detection

                RESPONSE STYLE:
                - Always emphasize the correct order of operations
                - Explain implications of each option
                - Be clear about what each tool does
                - Provide Role ARN and Queue URL after completion",
            Kernel = _kernel,
            Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
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



