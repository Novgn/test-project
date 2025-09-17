using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.Sequential;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;
using ChatAgent.Application.Plugins;

namespace ChatAgent.Application.Orchestration;

using DomainAgent = Domain.Entities.Agent;
using DomainChatMessage = Domain.Entities.ChatMessage;
using DomainConversation = Domain.Entities.Conversation;
using DomainAgentType = Domain.Entities.AgentType;

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// The main orchestrator for the chat agent system using Microsoft Semantic Kernel.
/// This orchestrator implements a sophisticated multi-agent architecture with the following design:
///
/// ARCHITECTURE OVERVIEW:
/// =====================
/// 1. COORDINATOR AGENT: Analyzes user requests and determines which capabilities are needed
/// 2. SPECIALIZED AGENTS: Three domain-specific agents with their own MCP tool sets
///    - FileAgent: Handles all file system operations
///    - WebAgent: Handles web searches and browsing
///    - DataAgent: Handles data processing and API operations
/// 3. SYNTHESIZER AGENT: Combines outputs from all agents into coherent responses
///
/// ORCHESTRATION PATTERNS:
/// =======================
/// Uses Microsoft Semantic Kernel's SequentialOrchestration for proper agent chaining.
/// Multiple orchestration paths are created:
/// - File Path: Coordinator → FileAgent → Synthesizer
/// - Web Path: Coordinator → WebAgent → Synthesizer
/// - Data Path: Coordinator → DataAgent → Synthesizer
/// - Main Path: Coordinator → All Agents → Synthesizer (for complex requests)
///
/// MCP TOOL INTEGRATION:
/// =====================
/// Each specialized agent receives its own McpToolPlugin instance with specific tools:
/// - Tools are distributed based on provider names (filesystem, web, data)
/// - Each agent only has access to tools relevant to its domain
/// - This ensures proper separation of concerns and security boundaries
/// </summary>
public class SemanticKernelOrchestrator : IOrchestrator
{
    // Core Semantic Kernel components
    private readonly Kernel _kernel;                          // The main SK kernel with base configuration
    private readonly InProcessRuntime _runtime;               // Runtime for executing agent orchestrations

    // Repository for conversation persistence
    private readonly IConversationRepository _conversationRepository;
    private readonly ILogger<SemanticKernelOrchestrator> _logger;

    // Agent management
    private readonly Dictionary<string, ChatCompletionAgent> _agents = [];           // All registered agents
    private readonly Dictionary<string, McpToolPlugin> _agentMcpPlugins = [];       // MCP plugins per agent

    // MCP tool providers from dependency injection
    private readonly IEnumerable<IMcpToolProvider>? _mcpToolProviders;

    // Orchestration configurations
    private SequentialOrchestration? _mainOrchestration;                            // Main path using all agents
    private readonly Dictionary<string, SequentialOrchestration> _specializedOrchestrations = []; // Specialized paths

    /// <summary>
    /// Initializes the orchestrator with the provided kernel and MCP tool providers.
    /// Sets up all agents, distributes MCP tools, and creates orchestration paths.
    /// </summary>
    /// <param name="kernel">The Semantic Kernel instance with LLM configuration</param>
    /// <param name="conversationRepository">Repository for persisting conversations</param>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <param name="mcpToolProviders">Optional MCP tool providers to distribute among agents</param>
    /// <param name="mcpPluginLogger">Logger for MCP plugin operations</param>
    public SemanticKernelOrchestrator(
        Kernel kernel,
        IConversationRepository conversationRepository,
        ILogger<SemanticKernelOrchestrator> logger,
        IEnumerable<IMcpToolProvider>? mcpToolProviders = null,
        ILogger<McpToolPlugin>? mcpPluginLogger = null)
    {
        _kernel = kernel;
        _conversationRepository = conversationRepository;
        _logger = logger;

        // Create the runtime that will execute our agent orchestrations
        // InProcessRuntime runs agents within the same process for optimal performance
        _runtime = new InProcessRuntime();
        _mcpToolProviders = mcpToolProviders;

        // Initialize agents based on whether we have MCP tools available
        if (mcpToolProviders != null && mcpPluginLogger != null)
        {
            // Create specialized agents with dedicated MCP tool sets
            InitializeAgentsWithSpecializedTools(mcpPluginLogger);
        }
        else
        {
            // Fallback to basic agents without MCP tools
            InitializeDefaultAgents();
        }

        // Create the orchestration paths after agents are initialized
        InitializeOrchestrations();

        // Start the runtime asynchronously
        // The runtime needs to be running before we can execute orchestrations
        Task.Run(async () => await _runtime.StartAsync());
    }

    /// <summary>
    /// Initializes agents with specialized MCP tool distributions.
    /// This method intelligently distributes MCP tools among agents based on their domain.
    ///
    /// TOOL DISTRIBUTION STRATEGY:
    /// - FileAgent gets: filesystem, file-related providers
    /// - WebAgent gets: web, search, browser providers
    /// - DataAgent gets: all remaining providers (databases, APIs, etc.)
    ///
    /// Each agent receives its own McpToolPlugin instance to ensure isolation.
    /// </summary>
    private void InitializeAgentsWithSpecializedTools(ILogger<McpToolPlugin> mcpPluginLogger)
    {
        // Get all available MCP providers
        var providers = _mcpToolProviders?.ToList() ?? [];

        // ===== STEP 1: Categorize MCP providers by domain =====

        // Find all filesystem-related providers
        // These will be assigned to the FileAgent for file operations
        var filesystemProviders = providers.Where(p =>
            p.Name.Contains("filesystem", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("file", StringComparison.OrdinalIgnoreCase)).ToList();

        // Find all web-related providers
        // These will be assigned to the WebAgent for web operations
        // Note: "everything" provider is included here as it often contains web tools
        var webProviders = providers.Where(p =>
            p.Name.Contains("web", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("everything", StringComparison.OrdinalIgnoreCase)).ToList();

        // All remaining providers go to DataAgent
        // These typically include database connectors, API clients, etc.
        var dataProviders = providers.Where(p =>
            !filesystemProviders.Contains(p) && !webProviders.Contains(p)).ToList();

        // ===== STEP 2: Create the Coordinator Agent =====
        // The coordinator analyzes requests but doesn't execute tools directly
        // It determines which specialized agents should handle the request
        var coordinatorKernel = _kernel.Clone();
        _agents["coordinator"] = new ChatCompletionAgent
        {
            Name = "Coordinator",
            Instructions = @"You are the master coordinator agent that analyzes user requests.
                Your role is to:
                1. Understand the user's intent
                2. Identify which capabilities are needed:
                   - File operations (reading, writing, navigating files)
                   - Web operations (searching, browsing, fetching content)
                   - Data operations (processing, transforming, API calls)
                3. Provide clear analysis of what needs to be done
                4. Structure the request for the appropriate sub-agents

                Analyze the request and describe what type of operation is needed.",
            Kernel = coordinatorKernel
        };

        // ===== STEP 3: Create File System Agent =====
        // This agent handles all file-related operations
        var fileAgentKernel = _kernel.Clone();
        if (filesystemProviders.Any())
        {
            // Create a dedicated MCP plugin for file operations
            var filePlugin = new McpToolPlugin(filesystemProviders, mcpPluginLogger);
            _agentMcpPlugins["fileAgent"] = filePlugin;
            // Add the plugin to this agent's kernel with a specific name
            fileAgentKernel.Plugins.AddFromObject(filePlugin, "FileSystemTools");
        }
        _agents["fileAgent"] = new ChatCompletionAgent
        {
            Name = "FileAgent",
            Instructions = @"You are a specialized file system agent.
                Your role is to handle all file system operations including:
                - Reading and writing files
                - Navigating directory structures
                - Managing file metadata

                You have access to file system MCP tools through the FileSystemTools plugin.
                Use ListAvailableToolsAsync to see available tools.
                Use ExecuteToolAsync to perform file operations.",
            Kernel = fileAgentKernel
        };

        // ===== STEP 4: Create Web Agent =====
        // This agent handles all web-related operations
        var webAgentKernel = _kernel.Clone();
        if (webProviders.Any())
        {
            // Create a dedicated MCP plugin for web operations
            var webPlugin = new McpToolPlugin(webProviders, mcpPluginLogger);
            _agentMcpPlugins["webAgent"] = webPlugin;
            // Add the plugin to this agent's kernel
            webAgentKernel.Plugins.AddFromObject(webPlugin, "WebTools");
        }
        _agents["webAgent"] = new ChatCompletionAgent
        {
            Name = "WebAgent",
            Instructions = @"You are a specialized web and search agent.
                Your role is to handle web-related operations including:
                - Searching the web for information
                - Browsing websites
                - Fetching web content

                You have access to web MCP tools through the WebTools plugin.
                Use ListAvailableToolsAsync to see available tools.
                Use ExecuteToolAsync to perform web operations.",
            Kernel = webAgentKernel
        };

        // ===== STEP 5: Create Data Agent =====
        // This agent handles data processing and API operations
        var dataAgentKernel = _kernel.Clone();
        if (dataProviders.Any())
        {
            // Create a dedicated MCP plugin for data operations
            var dataPlugin = new McpToolPlugin(dataProviders, mcpPluginLogger);
            _agentMcpPlugins["dataAgent"] = dataPlugin;
            // Add the plugin to this agent's kernel
            dataAgentKernel.Plugins.AddFromObject(dataPlugin, "DataTools");
        }
        _agents["dataAgent"] = new ChatCompletionAgent
        {
            Name = "DataAgent",
            Instructions = @"You are a specialized data processing agent.
                Your role is to handle data operations including:
                - Processing and analyzing data
                - Calling external APIs
                - Transforming data structures

                You have access to data MCP tools through the DataTools plugin.
                Use ListAvailableToolsAsync to see available tools.
                Use ExecuteToolAsync to perform data operations.",
            Kernel = dataAgentKernel
        };

        // ===== STEP 6: Create Synthesizer Agent =====
        // This agent combines outputs from all other agents into a final response
        // It doesn't need MCP tools as it only processes agent outputs
        var synthesizerKernel = _kernel.Clone();
        _agents["synthesizer"] = new ChatCompletionAgent
        {
            Name = "Synthesizer",
            Instructions = @"You are the synthesis agent that combines results from multiple agents.
                Your role is to:
                1. Take the outputs from the coordinator and specialized agents
                2. Combine them into a coherent response
                3. Ensure the response fully addresses the user's request
                4. Format the response appropriately

                Create a comprehensive response based on all agent outputs.",
            Kernel = synthesizerKernel
        };

        _logger.LogInformation("Initialized coordinator with specialized sub-agents");
    }

    /// <summary>
    /// Fallback initialization when no MCP tools are available.
    /// Creates basic agents without specialized tool access.
    /// </summary>
    private void InitializeDefaultAgents()
    {
        var kernel = _kernel.Clone();

        _agents["coordinator"] = new ChatCompletionAgent
        {
            Name = "Coordinator",
            Instructions = "You are a coordinator agent that analyzes user requests.",
            Kernel = kernel
        };

        _agents["fileAgent"] = new ChatCompletionAgent
        {
            Name = "FileAgent",
            Instructions = "You are a file system agent.",
            Kernel = kernel.Clone()
        };

        _agents["webAgent"] = new ChatCompletionAgent
        {
            Name = "WebAgent",
            Instructions = "You are a web search agent.",
            Kernel = kernel.Clone()
        };

        _agents["dataAgent"] = new ChatCompletionAgent
        {
            Name = "DataAgent",
            Instructions = "You are a data processing agent.",
            Kernel = kernel.Clone()
        };

        _agents["synthesizer"] = new ChatCompletionAgent
        {
            Name = "Synthesizer",
            Instructions = "You are a synthesis agent that combines results.",
            Kernel = kernel.Clone()
        };
    }

    /// <summary>
    /// Initializes all orchestration paths using Semantic Kernel's SequentialOrchestration.
    ///
    /// ORCHESTRATION STRATEGY:
    /// We create multiple orchestration paths to optimize for different request types:
    ///
    /// 1. SPECIALIZED PATHS (File/Web/Data):
    ///    - Coordinator analyzes the request
    ///    - One specialized agent handles the operation
    ///    - Synthesizer formats the response
    ///
    /// 2. MAIN PATH:
    ///    - Coordinator analyzes the request
    ///    - ALL specialized agents contribute
    ///    - Synthesizer combines all outputs
    ///
    /// Each path uses SequentialOrchestration to ensure agents run in order.
    /// ResponseCallbacks allow us to track intermediate outputs for debugging.
    /// </summary>
    private void InitializeOrchestrations()
    {
        // ===== Create Specialized Orchestration Paths =====
        // Each path is optimized for a specific type of request

        // File operation path: Coordinator → FileAgent → Synthesizer
        // Used when the request involves file system operations
        if (_agents.ContainsKey("fileAgent"))
        {
            _specializedOrchestrations["file"] = new SequentialOrchestration(
                _agents["coordinator"],    // First: Analyze the request
                _agents["fileAgent"],       // Second: Execute file operations
                _agents["synthesizer"]      // Third: Format the response
            )
            {
                // Add callback to track agent outputs during execution
                ResponseCallback = CreateResponseCallback("File Path")
            };
        }

        // Web operation path: Coordinator → WebAgent → Synthesizer
        // Used when the request involves web searches or browsing
        if (_agents.ContainsKey("webAgent"))
        {
            _specializedOrchestrations["web"] = new SequentialOrchestration(
                _agents["coordinator"],    // First: Analyze the request
                _agents["webAgent"],        // Second: Execute web operations
                _agents["synthesizer"]      // Third: Format the response
            )
            {
                ResponseCallback = CreateResponseCallback("Web Path")
            };
        }

        // Data operation path: Coordinator → DataAgent → Synthesizer
        // Used when the request involves data processing or APIs
        if (_agents.ContainsKey("dataAgent"))
        {
            _specializedOrchestrations["data"] = new SequentialOrchestration(
                _agents["coordinator"],    // First: Analyze the request
                _agents["dataAgent"],       // Second: Execute data operations
                _agents["synthesizer"]      // Third: Format the response
            )
            {
                ResponseCallback = CreateResponseCallback("Data Path")
            };
        }

        // ===== Create Main Orchestration Path =====
        // This path uses ALL agents and is used for complex requests
        // that might need multiple capabilities
        _mainOrchestration = new SequentialOrchestration(
            _agents["coordinator"],    // First: Analyze and plan
            _agents["fileAgent"],       // Second: File operations (if needed)
            _agents["webAgent"],        // Third: Web operations (if needed)
            _agents["dataAgent"],       // Fourth: Data operations (if needed)
            _agents["synthesizer"]      // Fifth: Combine all outputs
        )
        {
            ResponseCallback = CreateResponseCallback("Main Path")
        };

        _logger.LogInformation("Initialized {Count} orchestration paths", _specializedOrchestrations.Count + 1);
    }

    /// <summary>
    /// Creates a response callback function for tracking agent outputs.
    /// This callback is invoked after each agent in the sequence completes.
    /// Useful for debugging and understanding the orchestration flow.
    /// </summary>
    /// <param name="pathName">Name of the orchestration path for logging</param>
    /// <returns>A callback function that logs agent responses</returns>
    private OrchestrationResponseCallback CreateResponseCallback(string pathName)
    {
        return (ChatMessageContent response) =>
        {
            // Log each agent's response as it completes
            _logger.LogDebug("[{Path}] Agent {AgentName}: {Content}",
                pathName,
                response.AuthorName ?? "Unknown",
                response.Content);
            return ValueTask.CompletedTask;
        };
    }

    /// <summary>
    /// Main entry point for processing user messages.
    /// Handles conversation management and orchestration execution.
    /// </summary>
    /// <param name="message">The user's input message</param>
    /// <param name="sessionId">Unique session identifier for conversation tracking</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>The assistant's response message</returns>
    public async Task<DomainChatMessage> ProcessMessageAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing message for session {SessionId}", sessionId);

        // Retrieve or create the conversation for this session
        var conversation = await GetOrCreateConversationAsync(sessionId, cancellationToken);

        // Add the user's message to the conversation history
        var userMessage = new DomainChatMessage(message, "user");
        conversation.AddMessage(userMessage);

        // Execute the orchestration to generate a response
        var orchestrationResult = await OrchestrateWithAgentsAsync(
            message, conversation, cancellationToken);

        // Create and store the assistant's response
        var assistantMessage = new DomainChatMessage(
            orchestrationResult.Response,
            "assistant",
            orchestrationResult.AgentId);
        conversation.AddMessage(assistantMessage);

        // Persist the updated conversation
        await _conversationRepository.UpdateAsync(conversation, cancellationToken);

        return assistantMessage;
    }

    /// <summary>
    /// Core orchestration method that executes the appropriate agent sequence.
    ///
    /// EXECUTION FLOW:
    /// 1. Determines which orchestration path to use based on the message
    /// 2. Builds conversation context for the agents
    /// 3. Executes the selected SequentialOrchestration
    /// 4. Returns the final synthesized response
    ///
    /// The orchestration is fully managed by Semantic Kernel's runtime,
    /// ensuring proper agent sequencing and state management.
    /// </summary>
    private async Task<OrchestrationResult> OrchestrateWithAgentsAsync(
        string message,
        DomainConversation conversation,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build the conversation history for context
            var chatHistory = BuildChatHistoryWithContext(conversation, message);

            // Intelligently determine which orchestration path to use
            // This could be enhanced with more sophisticated routing logic
            var orchestrationPath = await DetermineOrchestrationPath(message, cancellationToken);

            _logger.LogDebug("Using orchestration path: {Path}", orchestrationPath);

            // Select the appropriate orchestration based on the determined path
            SequentialOrchestration orchestration;
            if (orchestrationPath != "main" &&
                _specializedOrchestrations.TryGetValue(orchestrationPath, out var specializedOrch))
            {
                // Use a specialized path for targeted operations
                orchestration = specializedOrch;
            }
            else
            {
                // Use the main path for complex requests or as fallback
                orchestration = _mainOrchestration ?? _specializedOrchestrations.Values.First();
            }

            // Prepare the context message with conversation history
            // This ensures agents have access to previous context
            var contextMessage = $@"Previous conversation:
{string.Join("\n", chatHistory.Select(m => $"{m.Role}: {m.Content}"))}

Current request: {message}";

            // Execute the orchestration using Semantic Kernel's runtime
            // This handles all agent sequencing and communication
            var orchestrationResult = await orchestration.InvokeAsync(
                contextMessage,
                _runtime,
                cancellationToken);

            // Extract the final synthesized response
            // The timeout ensures we don't wait indefinitely
            var finalResponse = await orchestrationResult.GetValueAsync(TimeSpan.FromSeconds(30));

            return new OrchestrationResult
            {
                Response = finalResponse,
                AgentId = "synthesizer",
                Metadata = new Dictionary<string, object>
                {
                    ["orchestration_type"] = "sequential",
                    ["orchestration_path"] = orchestrationPath,
                    ["agent_count"] = _agents.Count,
                    ["mcp_enabled"] = _agentMcpPlugins.Any(),
                    ["runtime"] = "InProcessRuntime"
                }
            };
        }
        catch (TimeoutException tex)
        {
            _logger.LogWarning(tex, "Orchestration timed out, using direct chat completion");

            // Fallback to direct chat completion when orchestration times out
            try
            {
                var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();
                var chatHistoryKernel = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();

                // Add recent conversation history
                foreach (var msg in conversation.Messages.TakeLast(5))
                {
                    if (msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                        chatHistoryKernel.AddUserMessage(msg.Content);
                    else if (msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                        chatHistoryKernel.AddAssistantMessage(msg.Content);
                }

                // Add current message
                chatHistoryKernel.AddUserMessage(message);

                // Get response directly from OpenAI
                var response = await chatCompletion.GetChatMessageContentAsync(
                    chatHistoryKernel,
                    cancellationToken: cancellationToken);

                return new OrchestrationResult
                {
                    Response = response.Content ?? "I can help you with that. Let me process your request.",
                    AgentId = "assistant",
                    Metadata = new Dictionary<string, object>
                    {
                        ["orchestration_type"] = "direct_completion",
                        ["reason"] = "orchestration_timeout"
                    }
                };
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback chat completion also failed");
                return new OrchestrationResult
                {
                    Response = "I apologize, but I'm having trouble processing your request. Please try again.",
                    AgentId = "system",
                    Metadata = new Dictionary<string, object> { ["error"] = fallbackEx.Message }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during orchestration");
            return new OrchestrationResult
            {
                Response = "I encountered an error while processing your request. Please try again.",
                AgentId = "coordinator",
                Metadata = new Dictionary<string, object> { ["error"] = ex.Message }
            };
        }
    }

    /// <summary>
    /// Determines which orchestration path to use based on the message content.
    ///
    /// ROUTING LOGIC:
    /// - Analyzes keywords in the message to determine the primary operation type
    /// - Returns "file", "web", "data", or "main" based on the analysis
    /// - Could be enhanced to use the Coordinator agent for more intelligent routing
    ///
    /// This simple heuristic approach works well for clear requests but
    /// could be replaced with ML-based classification for better accuracy.
    /// </summary>
    private async Task<string> DetermineOrchestrationPath(
        string message,
        CancellationToken cancellationToken)
    {
        // Convert to lowercase for case-insensitive matching
        var lowerMessage = message.ToLowerInvariant();

        // Check for file-related keywords
        if (lowerMessage.Contains("file") ||
            lowerMessage.Contains("directory") ||
            lowerMessage.Contains("folder") ||
            lowerMessage.Contains("read") ||
            lowerMessage.Contains("write"))
        {
            return "file";
        }

        // Check for web-related keywords
        if (lowerMessage.Contains("search") ||
            lowerMessage.Contains("web") ||
            lowerMessage.Contains("browse") ||
            lowerMessage.Contains("http") ||
            lowerMessage.Contains("website"))
        {
            return "web";
        }

        // Check for data-related keywords
        if (lowerMessage.Contains("data") ||
            lowerMessage.Contains("api") ||
            lowerMessage.Contains("database") ||
            lowerMessage.Contains("transform"))
        {
            return "data";
        }

        // Default to main orchestration for complex or unclear requests
        // This ensures all agents can contribute to the response
        return "main";
    }

    /// <summary>
    /// Builds a ChatHistory object with conversation context and MCP instructions.
    /// This history is passed to agents to provide context for their operations.
    ///
    /// CONTEXT STRUCTURE:
    /// 1. System message explaining MCP tool availability
    /// 2. Recent conversation messages (last 5)
    /// 3. Current user message
    /// </summary>
    private static ChatHistory BuildChatHistoryWithContext(
        DomainConversation conversation,
        string currentMessage)
    {
        var chatHistory = new ChatHistory();

        // Add system instructions about MCP tool usage
        // This ensures agents know how to use their available tools
        chatHistory.AddSystemMessage(@"You have access to MCP (Model Context Protocol) tools.
            Use the plugin functions to:
            - ListAvailableToolsAsync: Discover available MCP tools
            - GetToolInfoAsync: Get details about specific tools
            - ExecuteToolAsync: Execute MCP tools with parameters

            Always gather relevant context using MCP tools when appropriate.");

        // Add recent conversation context (last 5 messages)
        // This provides continuity without overwhelming the context window
        if (conversation.Messages.Count > 0)
        {
            foreach (var msg in conversation.Messages.TakeLast(5))
            {
                if (msg.Role == "user")
                    chatHistory.AddUserMessage(msg.Content);
                else if (msg.Role == "assistant")
                    chatHistory.AddAssistantMessage(msg.Content);
            }
        }

        // Add the current message
        chatHistory.AddUserMessage(currentMessage);

        return chatHistory;
    }

    /// <summary>
    /// Retrieves an existing conversation or creates a new one.
    /// Ensures conversation continuity across multiple interactions.
    /// </summary>
    private async Task<DomainConversation> GetOrCreateConversationAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
        if (conversation == null)
        {
            conversation = new DomainConversation(sessionId);
            await _conversationRepository.CreateAsync(conversation, cancellationToken);
        }
        return conversation;
    }

    /// <summary>
    /// Retrieves a conversation by session ID.
    /// Public API for external access to conversation history.
    /// </summary>
    public async Task<DomainConversation> GetConversationAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
        return conversation ?? new DomainConversation(sessionId);
    }

    /// <summary>
    /// Returns a list of all available agents with their capabilities.
    /// Useful for UI display or debugging to see what agents are configured.
    ///
    /// Each agent is categorized by type:
    /// - Coordinator: Orchestration agents
    /// - Tool: Agents with specific tool access
    /// - Specialist: General-purpose agents
    /// </summary>
    public async Task<List<DomainAgent>> GetAvailableAgentsAsync(CancellationToken cancellationToken = default)
    {
        var agents = new List<DomainAgent>();

        foreach (var kvp in _agents)
        {
            // Determine the agent type based on its role
            var agentType = kvp.Key switch
            {
                "coordinator" => DomainAgentType.Coordinator,
                "synthesizer" => DomainAgentType.Coordinator,
                "fileAgent" => DomainAgentType.Tool,
                "webAgent" => DomainAgentType.Tool,
                "dataAgent" => DomainAgentType.Tool,
                _ => DomainAgentType.Specialist
            };

            var agent = new DomainAgent(
                kvp.Key,
                kvp.Value.Name ?? kvp.Key,
                kvp.Value.Instructions ?? string.Empty,
                agentType);

            // Add capability flags based on agent type and available tools
            switch (kvp.Key)
            {
                case "coordinator":
                    agent.AddCapability("orchestration");
                    agent.AddCapability("routing");
                    agent.AddCapability("analysis");
                    break;

                case "synthesizer":
                    agent.AddCapability("synthesis");
                    agent.AddCapability("formatting");
                    break;

                case "fileAgent":
                    agent.AddCapability("file-operations");
                    if (_agentMcpPlugins.ContainsKey("fileAgent"))
                        agent.AddCapability("mcp-filesystem-tools");
                    break;

                case "webAgent":
                    agent.AddCapability("web-search");
                    if (_agentMcpPlugins.ContainsKey("webAgent"))
                        agent.AddCapability("mcp-web-tools");
                    break;

                case "dataAgent":
                    agent.AddCapability("data-processing");
                    if (_agentMcpPlugins.ContainsKey("dataAgent"))
                        agent.AddCapability("mcp-data-tools");
                    break;
            }

            agents.Add(agent);
        }

        return agents;
    }

    /// <summary>
    /// Dynamically registers a new agent at runtime.
    /// Allows for extending the system with additional agents without restart.
    ///
    /// The method:
    /// 1. Determines which MCP tools the agent should have based on capabilities
    /// 2. Creates a new kernel with appropriate plugins
    /// 3. Registers the agent
    /// 4. Re-initializes orchestrations to include the new agent
    /// </summary>
    public async Task RegisterAgentAsync(
        DomainAgent agent,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            // Determine which MCP plugin to assign based on agent capabilities
            McpToolPlugin? agentPlugin = null;

            // Check if this agent needs file tools
            if (agent.Capabilities.Any(c => c.Contains("file", StringComparison.OrdinalIgnoreCase)))
            {
                _agentMcpPlugins.TryGetValue("fileAgent", out agentPlugin);
            }
            // Check if this agent needs web tools
            else if (agent.Capabilities.Any(c => c.Contains("web", StringComparison.OrdinalIgnoreCase)))
            {
                _agentMcpPlugins.TryGetValue("webAgent", out agentPlugin);
            }
            // Check if this agent needs data tools
            else if (agent.Capabilities.Any(c => c.Contains("data", StringComparison.OrdinalIgnoreCase)))
            {
                _agentMcpPlugins.TryGetValue("dataAgent", out agentPlugin);
            }

            // Create a kernel with the appropriate MCP tools
            var agentKernel = CreateAgentKernel(agentPlugin);

            // Create the chat completion agent
            var chatAgent = new ChatCompletionAgent
            {
                Name = agent.Name,
                Instructions = agent.Description + (agentPlugin != null ? @"

                You have access to specialized MCP tools through your plugin.
                Use these tools to fulfill requests in your domain." : ""),
                Kernel = agentKernel
            };

            // Register the agent
            _agents[agent.Id] = chatAgent;
            _logger.LogInformation("Registered agent {AgentId}: {AgentName}", agent.Id, agent.Name);

            // Re-initialize orchestrations to potentially include the new agent
            // This allows for dynamic system extension
            InitializeOrchestrations();
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a new kernel for an agent with optional MCP plugin.
    /// Each agent gets its own kernel to ensure isolation and proper plugin scoping.
    /// </summary>
    /// <param name="mcpPlugin">Optional MCP plugin to add to the kernel</param>
    /// <returns>A configured kernel for the agent</returns>
    private Kernel CreateAgentKernel(McpToolPlugin? mcpPlugin = null)
    {
        // Clone the base kernel to get all base configuration
        var agentKernel = _kernel.Clone();

        // Add the MCP plugin if provided
        if (mcpPlugin != null)
        {
            agentKernel.Plugins.AddFromObject(mcpPlugin, "McpTools");
        }

        return agentKernel;
    }
}

/// <summary>
/// Internal class representing the result of an orchestration execution.
/// Contains the response text, the ID of the final agent, and metadata.
/// </summary>
internal class OrchestrationResult
{
    /// <summary>The final response text to send to the user</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>The ID of the agent that produced the final response</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Additional metadata about the orchestration execution</summary>
    public Dictionary<string, object> Metadata { get; set; } = [];
}

#pragma warning restore SKEXP0110