using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ChatAgent.Application.Orchestration;

#pragma warning disable SKEXP0001, SKEXP0110

/// <summary>
/// Selection strategy for coordinator-controlled group chat
/// Ensures coordinator always responds to user messages
/// </summary>
public class CoordinatorSelectionStrategy : SelectionStrategy
{
    private readonly ILogger? _logger;

    public CoordinatorSelectionStrategy(ILogger? logger = null)
    {
        _logger = logger;
    }

    protected override async Task<Agent> SelectAgentAsync(
        IReadOnlyList<Agent> agents,
        IReadOnlyList<ChatMessageContent> history,
        CancellationToken cancellationToken = default)
    {
        var lastMessage = history.LastOrDefault();
        if (lastMessage == null)
        {
            // Default to coordinator
            var coordinator = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
            return coordinator ?? agents.First();
        }

        _logger?.LogDebug("Last message from: {Author}", lastMessage.AuthorName);

        // User messages always go to coordinator
        if (lastMessage.AuthorName == "User")
        {
            var coordinator = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
            if (coordinator != null)
            {
                _logger?.LogDebug("User message detected, selecting CoordinatorAgent");
                return coordinator;
            }
        }

        // Coordinator can engage Azure agent when needed
        if (lastMessage.AuthorName == "CoordinatorAgent")
        {
            var content = lastMessage.Content?.ToLower() ?? "";

            // Check if coordinator is asking for Azure help
            if (content.Contains("azure") || content.Contains("find") || content.Contains("solution"))
            {
                var azureAgent = agents.FirstOrDefault(a => a.Name == "AzureAgent");
                if (azureAgent != null)
                {
                    _logger?.LogDebug("Coordinator needs Azure help, selecting AzureAgent");
                    return azureAgent;
                }
            }
        }

        // Azure agent responses go back to coordinator
        if (lastMessage.AuthorName == "AzureAgent")
        {
            var coordinator = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
            if (coordinator != null)
            {
                _logger?.LogDebug("Azure response detected, returning to CoordinatorAgent");
                return coordinator;
            }
        }

        // Default to coordinator
        var defaultCoordinator = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent") ?? agents.First();
        return defaultCoordinator;
    }
}

/// <summary>
/// Termination strategy for coordinator-controlled chat
/// Terminates after coordinator provides a response to user
/// </summary>
public class CoordinatorTerminationStrategy : TerminationStrategy
{
    protected override async Task<bool> ShouldAgentTerminateAsync(
        Agent agent,
        IReadOnlyList<ChatMessageContent> history,
        CancellationToken cancellationToken = default)
    {
        // Check if we have at least one user message and one coordinator response
        var hasUserMessage = history.Any(m => m.AuthorName == "User");
        var lastCoordinatorIndex = -1;
        var lastUserIndex = -1;

        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].AuthorName == "User")
            {
                lastUserIndex = i;
            }
            else if (history[i].AuthorName == "CoordinatorAgent")
            {
                lastCoordinatorIndex = i;
            }
        }

        // Terminate if coordinator has responded after the last user message
        var shouldTerminate = hasUserMessage &&
                              lastCoordinatorIndex > lastUserIndex &&
                              lastCoordinatorIndex == history.Count - 1;

        return shouldTerminate;
    }
}

#pragma warning restore SKEXP0001, SKEXP0110