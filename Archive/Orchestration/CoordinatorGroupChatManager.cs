/* ARCHIVED - No longer in use
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Logging;

namespace ChatAgent.Application.Orchestration;

#pragma warning disable SKEXP0001, SKEXP0110

/// <summary>
/// Custom group chat manager for coordinator-controlled conversation flow
/// Where the coordinator acts as the intermediary between user and specialized agents
/// </summary>
public class CoordinatorGroupChatManager : GroupChatManager
{
    private readonly ILogger? _logger;
    private int _invocationCount = 0;
    private readonly Dictionary<string, string> _agentNameToId = new();

    public void RegisterAgent(string name, string id)
    {
        _agentNameToId[name] = id;
        _logger?.LogDebug("Registered agent '{Name}' with ID '{Id}'", name, id);
    }

    public CoordinatorGroupChatManager(ILogger? logger = null)
    {
        _logger = logger;
        MaximumInvocationCount = 20; // Allow for longer conversations
    }

    /// <summary>
    /// Filter and format the final results when the chat is terminating
    /// </summary>
    public override ValueTask<GroupChatManagerResult<string>> FilterResults(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        // Extract the last coordinator response as the final result
        var coordinatorMessages = history
            .Where(m => m.AuthorName == "CoordinatorAgent")
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        var result = coordinatorMessages.Count > 0
            ? string.Join("\n", coordinatorMessages.TakeLast(1))
            : "Processing complete.";

        _logger?.LogDebug("Filtering results, found {Count} coordinator messages", coordinatorMessages.Count);

        return ValueTask.FromResult(new GroupChatManagerResult<string>(result)
        {
            Reason = "Coordinator response extracted"
        });
    }

    /// <summary>
    /// Select the next agent based on conversation context
    /// </summary>
    public override ValueTask<GroupChatManagerResult<string>> SelectNextAgent(
        ChatHistory history,
        GroupChatTeam team,
        CancellationToken cancellationToken = default)
    {
        _invocationCount++;

        var lastMessage = history.LastOrDefault();

        _logger?.LogDebug("SelectNextAgent invoked, message count: {Count}, last author: '{Author}', role: {Role}",
            history.Count, lastMessage?.AuthorName ?? "none", lastMessage?.Role.ToString() ?? "none");

        // Log registered agents
        _logger?.LogDebug("Registered agents: {Agents}",
            string.Join(", ", _agentNameToId.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        // Get the coordinator agent ID
        if (!_agentNameToId.TryGetValue("CoordinatorAgent", out var coordinatorId))
        {
            _logger?.LogError("CoordinatorAgent not found in registered agents!");
            return ValueTask.FromResult(new GroupChatManagerResult<string>(string.Empty)
            {
                Reason = "CoordinatorAgent not found"
            });
        }

        // If no messages, first message, user message, or message without author name (which is the initial task)
        if (lastMessage == null ||
            string.IsNullOrEmpty(lastMessage.AuthorName) ||
            lastMessage.AuthorName == "User" ||
            lastMessage.Role == AuthorRole.User)
        {
            _logger?.LogDebug("Selecting CoordinatorAgent with ID: {Id}", coordinatorId);
            // Return the agent ID - the orchestration needs the ID, not the name
            return ValueTask.FromResult(new GroupChatManagerResult<string>(coordinatorId)
            {
                Reason = "User message - routing to coordinator"
            });
        }

        // If coordinator just spoke, check if Azure agent is needed
        if (lastMessage.AuthorName == "CoordinatorAgent")
        {
            var content = lastMessage.Content?.ToLower() ?? "";

            // Check if coordinator is asking for Azure help
            if (content.Contains("find") && (content.Contains("solution") || content.Contains("connector")) ||
                content.Contains("azure") && content.Contains("check"))
            {
                if (_agentNameToId.TryGetValue("AzureAgent", out var azureId))
                {
                    _logger?.LogDebug("Coordinator needs Azure help, selecting AzureAgent with ID: {Id}", azureId);
                    return ValueTask.FromResult(new GroupChatManagerResult<string>(azureId)
                    {
                        Reason = "Coordinator requesting Azure operations"
                    });
                }
            }
        }

        // If Azure agent just spoke, return to coordinator
        if (lastMessage.AuthorName == "AzureAgent")
        {
            _logger?.LogDebug("Azure response detected, returning to CoordinatorAgent with ID: {Id}", coordinatorId);
            return ValueTask.FromResult(new GroupChatManagerResult<string>(coordinatorId)
            {
                Reason = "Azure agent completed - returning to coordinator"
            });
        }

        // Default to coordinator
        _logger?.LogDebug("Default selection - CoordinatorAgent with ID: {Id}", coordinatorId);
        return ValueTask.FromResult(new GroupChatManagerResult<string>(coordinatorId)
        {
            Reason = "Default selection - coordinator"
        });
    }

    /// <summary>
    /// Determine if user input is needed
    /// </summary>
    public override ValueTask<GroupChatManagerResult<bool>> ShouldRequestUserInput(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        // In this implementation, we don't request additional user input during execution
        return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
        {
            Reason = "Coordinator-controlled flow - no additional user input needed"
        });
    }

    /// <summary>
    /// Determine if the chat should terminate
    /// </summary>
    public override ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
        ChatHistory history,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("ShouldTerminate called, invocationCount: {Count}, historyCount: {HistoryCount}",
            _invocationCount, history.Count);

        // Log all messages in history for debugging
        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            _logger?.LogDebug("History[{Index}]: Role={Role}, Author='{Author}', Content length={Length}",
                i, msg.Role, msg.AuthorName ?? "null", msg.Content?.Length ?? 0);
        }

        // Check maximum invocations first
        if (_invocationCount >= MaximumInvocationCount)
        {
            _logger?.LogWarning("Maximum invocations ({Max}) reached", MaximumInvocationCount);
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(true)
            {
                Reason = $"Maximum invocations ({MaximumInvocationCount}) reached"
            });
        }

        // Need at least 2 messages to consider termination (user + agent response)
        if (history.Count < 2)
        {
            _logger?.LogDebug("Not enough messages to terminate (count: {Count})", history.Count);
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
            {
                Reason = "Not enough messages"
            });
        }

        // Check if we have at least one user message and one coordinator response
        var hasUserMessage = false;
        var lastCoordinatorIndex = -1;
        var lastUserIndex = -1;

        for (int i = 0; i < history.Count; i++)
        {
            var message = history[i];
            _logger?.LogDebug("Message {Index}: Role={Role}, Author='{Author}', Content length={Length}",
                i, message.Role, message.AuthorName ?? "null", message.Content?.Length ?? 0);

            // Consider messages without author name as user messages (initial task)
            if (string.IsNullOrEmpty(message.AuthorName) ||
                message.AuthorName == "User" ||
                message.Role == AuthorRole.User)
            {
                hasUserMessage = true;
                lastUserIndex = i;
            }
            else if (message.AuthorName == "CoordinatorAgent")
            {
                lastCoordinatorIndex = i;
            }
        }

        // Terminate if coordinator has responded after the last user message
        var shouldTerminate = hasUserMessage &&
                              lastCoordinatorIndex > lastUserIndex &&
                              lastCoordinatorIndex == history.Count - 1;

        _logger?.LogDebug("Termination check: hasUser={HasUser}, lastCoordinator={LastCoord}, lastUser={LastUser}, shouldTerminate={Should}",
            hasUserMessage, lastCoordinatorIndex, lastUserIndex, shouldTerminate);

        if (shouldTerminate)
        {
            _logger?.LogInformation("Terminating chat - Coordinator has responded to user");
            return ValueTask.FromResult(new GroupChatManagerResult<bool>(true)
            {
                Reason = "Coordinator has responded to user"
            });
        }

        return ValueTask.FromResult(new GroupChatManagerResult<bool>(false)
        {
            Reason = "Conversation continuing"
        });
    }
}

#pragma warning restore SKEXP0001, SKEXP0110*/
