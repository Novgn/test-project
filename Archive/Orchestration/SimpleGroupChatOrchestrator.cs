/* ARCHIVED - No longer in use
/* ARCHIVED - No longer in use
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.Extensions.Logging;
using ChatAgent.Domain.Interfaces;
using ChatAgent.Domain.Entities;

namespace ChatAgent.Application.Orchestration;

#pragma warning disable SKEXP0001, SKEXP0110

/// <summary>
/// Simplified group chat orchestrator to test the basic pattern
/// </summary>
public class SimpleGroupChatOrchestrator : IOrchestrator
{
    private readonly Kernel _kernel;
    private readonly ILogger<SimpleGroupChatOrchestrator> _logger;
    private readonly IConversationRepository _conversationRepository;

    public SimpleGroupChatOrchestrator(
        Kernel kernel,
        ILogger<SimpleGroupChatOrchestrator> logger,
        IConversationRepository conversationRepository)
    {
        _kernel = kernel;
        _logger = logger;
        _conversationRepository = conversationRepository;
    }

    public async Task<ChatMessage> ProcessMessageAsync(
        string message,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing message: {Message}", message);

        try
        {
            // Create two agents for the group chat (minimum needed for RoundRobinGroupChatManager)
            var writer = new ChatCompletionAgent
            {
                Name = "Writer",
                Description = "A helpful assistant",
                Instructions = "You are a helpful assistant. Provide a clear and concise response to the user's message.",
                Kernel = _kernel
            };

            var reviewer = new ChatCompletionAgent
            {
                Name = "Reviewer",
                Description = "A quality reviewer",
                Instructions = "You review responses and ensure they are helpful. If the response is good, simply say 'APPROVED'.",
                Kernel = _kernel
            };

            _logger.LogDebug("Created agents: {Writer} and {Reviewer}", writer.Name, reviewer.Name);

            // Create a simple round-robin manager
            var manager = new RoundRobinGroupChatManager
            {
                MaximumInvocationCount = 2 // One from writer, one from reviewer
            };

            _logger.LogDebug("Created RoundRobinGroupChatManager");

            // Create the orchestration with both agents
            var orchestration = new GroupChatOrchestration(manager, writer, reviewer);
            _logger.LogDebug("Created GroupChatOrchestration");

            // Create and start runtime
            var runtime = new InProcessRuntime();
            await runtime.StartAsync(cancellationToken);
            _logger.LogDebug("Runtime started");

            // Invoke orchestration
            _logger.LogInformation("Invoking orchestration with message: {Message}", message);
            var result = await orchestration.InvokeAsync(message, runtime, cancellationToken);
            _logger.LogDebug("Orchestration invoked");

            // Wait for the result with a timeout
            var response = await result.GetValueAsync(TimeSpan.FromSeconds(30));
            _logger.LogInformation("Got response: {Response}", response);

            // Clean up the runtime
            await runtime.RunUntilIdleAsync();
            _logger.LogDebug("Runtime ran until idle");

            // Store in repository
            var conversation = await GetOrCreateConversationAsync(sessionId, cancellationToken);
            var userMessage = new ChatMessage(message, "user");
            var assistantMessage = new ChatMessage(response ?? "I'm sorry, I couldn't process your message.", "assistant");

            conversation.AddMessage(userMessage);
            conversation.AddMessage(assistantMessage);
            await _conversationRepository.UpdateAsync(conversation, cancellationToken);

            return assistantMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            return new ChatMessage(
                "I encountered an error processing your message. Please try again.",
                "assistant",
                "system");
        }
    }

    private async Task<Conversation> GetOrCreateConversationAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
        if (conversation == null)
        {
            conversation = new Conversation(sessionId);
            await _conversationRepository.CreateAsync(conversation, cancellationToken);
        }
        return conversation;
    }

    public async Task<Conversation> GetConversationAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _conversationRepository.GetBySessionIdAsync(sessionId, cancellationToken);
        return conversation ?? new Conversation(sessionId);
    }

    public Task<List<Domain.Entities.Agent>> GetAvailableAgentsAsync(CancellationToken cancellationToken = default)
    {
        var agents = new List<Domain.Entities.Agent>
        {
            new Domain.Entities.Agent("assistant", "Assistant", "A helpful assistant", Domain.Entities.AgentType.Coordinator)
        };
        return Task.FromResult(agents);
    }

    public Task RegisterAgentAsync(Domain.Entities.Agent agent, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

#pragma warning restore SKEXP0001, SKEXP0110*/
/* End of archived code */
