using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace ChatAgent.Application.Orchestration;

#pragma warning disable SKEXP0001, SKEXP0110 // Suppress experimental feature warnings

/// <summary>
/// Custom termination strategy for Sentinel connector setup that handles errors gracefully
/// </summary>
public class SentinelSetupTerminationStrategy : TerminationStrategy
{
    private readonly int _maximumIterations;
    private readonly ILogger? _logger;
    private readonly HashSet<string> _completionPhrases;
    private readonly HashSet<string> _errorPhrases;
    private readonly HashSet<string> _errorIndicators;
    private int _iterationCount = 0;
    private int _errorCount = 0;
    private bool _hasDetectedErrors = false;
    private readonly List<string> _detectedErrors = new();

    public SentinelSetupTerminationStrategy(
        int maximumIterations = 50,
        ILogger? logger = null)
    {
        _maximumIterations = maximumIterations;
        _logger = logger;

        _completionPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SETUP COMPLETE",
            "FINAL REPORT GENERATED",
            "Setup completed successfully",
            "All tasks completed"
        };

        _errorPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CRITICAL ERROR",
            "FATAL ERROR",
            "Setup failed",
            "Unhandled exception",
            "KeyNotFoundException",
            "AmazonServiceException",
            "Unable to get IAM security credentials"
        };

        // Additional error indicators to detect function failures
        _errorIndicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "error",
            "failed",
            "exception",
            "not found",
            "unable to",
            "could not",
            "cannot",
            "invalid",
            "unauthorized",
            "forbidden",
            "denied",
            "fail:",
            "error:",
            "exception:"
        };
    }

    /// <summary>
    /// Determines whether an agent should terminate based on the chat history
    /// </summary>
    protected override async Task<bool> ShouldAgentTerminateAsync(
        Agent agent,
        IReadOnlyList<ChatMessageContent> history,
        CancellationToken cancellationToken = default)
    {
        _iterationCount++;

        // Check maximum iterations
        if (_iterationCount >= _maximumIterations)
        {
            _logger?.LogWarning("Maximum iterations ({MaxIterations}) reached, terminating", _maximumIterations);
            return await Task.FromResult(true);
        }

        // Check the last few messages for completion or error indicators
        if (history.Count > 0)
        {
            // Check last 5 messages to catch error patterns
            var recentMessages = history.TakeLast(5).ToList();

            foreach (var message in recentMessages)
            {
                var content = message.Content ?? string.Empty;

                // Detect error patterns in the content
                if (DetectErrorsInContent(content))
                {
                    _errorCount++;
                    _hasDetectedErrors = true;
                    _logger?.LogWarning("Error detected in message from {Agent}: {ErrorSnippet}",
                        message.AuthorName,
                        content.Substring(0, Math.Min(200, content.Length)));

                    // If we see multiple errors, terminate to prevent false success
                    if (_errorCount >= 3)
                    {
                        _logger?.LogError("Multiple errors detected ({ErrorCount}), terminating to prevent false success claims", _errorCount);
                        return await Task.FromResult(true);
                    }
                }

                // Don't allow completion if errors have been detected
                if (_hasDetectedErrors && _completionPhrases.Any(phrase =>
                    content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger?.LogError("Agent trying to claim completion despite errors. Preventing false success.");
                    // Don't terminate on false success - let error handling take over
                    continue;
                }

                // Check for critical errors that should terminate immediately
                if (_errorPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger?.LogError("Critical error detected in message from {Agent}", message.AuthorName);
                    return await Task.FromResult(true);
                }

                // Only allow successful completion if no errors detected
                if (!_hasDetectedErrors && _completionPhrases.Any(phrase =>
                    content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger?.LogInformation("Legitimate setup completion detected from {Agent}", message.AuthorName);
                    return await Task.FromResult(true);
                }

                // Filter out and log invalid function call attempts
                if (IsInvalidFunctionCall(content))
                {
                    _logger?.LogDebug("Detected invalid function call attempt from {Agent}: {Content}",
                        message.AuthorName, content.Substring(0, Math.Min(100, content.Length)));
                }
            }

            // Check if conversation is stuck in error loop
            if (IsStuckInErrorLoop(recentMessages))
            {
                _logger?.LogError("Conversation stuck in error loop, terminating");
                return await Task.FromResult(true);
            }
        }

        // Continue by default
        return await Task.FromResult(false);
    }

    private bool DetectErrorsInContent(string content)
    {
        // Check for explicit error indicators
        foreach (var indicator in _errorIndicators)
        {
            if (content.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                // Avoid false positives on common phrases
                if (!IsCommonPhrase(content, indicator))
                {
                    _detectedErrors.Add($"Error indicator '{indicator}' found");
                    return true;
                }
            }
        }

        // Check for stack traces or exception patterns
        if (Regex.IsMatch(content, @"at\s+\w+\.\w+.*\(.*\)", RegexOptions.IgnoreCase))
        {
            _detectedErrors.Add("Stack trace detected");
            return true;
        }

        // Check for JSON error responses
        if (content.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("\"success\": false", StringComparison.OrdinalIgnoreCase))
        {
            _detectedErrors.Add("Failed response detected");
            return true;
        }

        return false;
    }

    private bool IsCommonPhrase(string content, string indicator)
    {
        // Avoid false positives on common non-error phrases
        var safeContexts = new[]
        {
            "no errors",
            "without error",
            "error-free",
            "error handling",
            "in case of error",
            "if error",
            "handle error"
        };

        return safeContexts.Any(safe =>
            content.Contains(safe, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsStuckInErrorLoop(List<ChatMessageContent> recentMessages)
    {
        if (recentMessages.Count < 3) return false;

        // Check if the same error is repeating
        var errorPatterns = new List<string>();
        foreach (var msg in recentMessages)
        {
            var content = msg.Content ?? string.Empty;

            // Extract error messages
            var match = Regex.Match(content, @"(exception|error):\s*(.+?)(?:\r?\n|$)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                errorPatterns.Add(match.Groups[2].Value.Trim());
            }
        }

        // If the same error appears 3+ times, we're stuck
        var duplicates = errorPatterns
            .GroupBy(x => x)
            .Where(g => g.Count() >= 3);

        return duplicates.Any();
    }

    private bool IsInvalidFunctionCall(string content)
    {
        // Check for patterns that indicate an agent is trying to call another agent as a function
        var invalidPatterns = new[]
        {
            @"AwsAgent\s*\(",
            @"AzureAgent\s*\(",
            @"CoordinatorAgent\s*\(",
            @"IntegrationAgent\s*\(",
            @"MonitorAgent\s*\(",
            @"""function""\s*:\s*""(Aws|Azure|Coordinator|Integration|Monitor)Agent""",
            @"{\s*""name""\s*:\s*""(Aws|Azure|Coordinator)Agent"""
        };

        return invalidPatterns.Any(pattern =>
            Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase));
    }

    public void Reset()
    {
        _iterationCount = 0;
        _errorCount = 0;
        _hasDetectedErrors = false;
        _detectedErrors.Clear();
    }

    public bool HasErrors => _hasDetectedErrors;
    public IReadOnlyList<string> DetectedErrors => _detectedErrors.AsReadOnly();
}

/// <summary>
/// Custom selection strategy that ensures proper agent ordering for Sentinel setup
/// </summary>
public class SentinelSetupSelectionStrategy : SelectionStrategy
{
    private readonly ILogger? _logger;
    private readonly Dictionary<string, int> _agentPriorities;
    private string? _lastAgentName;
    private int _roundRobinIndex = 0;
    private readonly Dictionary<string, int> _agentParticipationCount = new();
    private readonly Dictionary<string, int> _agentErrorCount = new();
    private bool _inErrorRecoveryMode = false;

    public SentinelSetupSelectionStrategy(ILogger? logger = null)
    {
        _logger = logger;

        // Define agent priorities for different phases
        _agentPriorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["CoordinatorAgent"] = 1,
            ["AwsAgent"] = 2,
            ["AzureAgent"] = 3,
            ["IntegrationAgent"] = 4,
            ["MonitorAgent"] = 5
        };
    }

    /// <summary>
    /// Selects the next agent to participate in the conversation
    /// </summary>
    protected override async Task<Agent> SelectAgentAsync(
        IReadOnlyList<Agent> agents,
        IReadOnlyList<ChatMessageContent> history,
        CancellationToken cancellationToken = default)
    {
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("No agents available for selection");
        }

        // Track agent participation and errors
        foreach (var agent in agents)
        {
            var agentName = agent.Name ?? "Unknown";
            if (!_agentParticipationCount.ContainsKey(agentName))
            {
                _agentParticipationCount[agentName] = 0;
                _agentErrorCount[agentName] = 0;
            }
        }

        // Check for errors in recent history
        CheckForErrors(history);

        // Analyze the conversation to determine the current phase
        var currentPhase = DeterminePhase(history);

        // Check if we need to prevent the same agent from going twice
        var lastMessageAgent = history.LastOrDefault()?.AuthorName;

        Agent? selectedAgent = null;

        // If in error recovery mode, prefer coordinator
        if (_inErrorRecoveryMode)
        {
            selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
            _logger?.LogInformation("Error recovery mode - selecting coordinator to handle errors");
        }
        else
        {
            switch (currentPhase)
            {
                case "initialization":
                    selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    break;

                case "validation":
                    selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    break;

                case "planning":
                    selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    break;

                case "aws-setup":
                    // Only select AWS agent if it hasn't had too many errors
                    var awsAgent = agents.FirstOrDefault(a => a.Name == "AwsAgent");
                    if (awsAgent != null && _agentErrorCount["AwsAgent"] < 3)
                    {
                        selectedAgent = awsAgent;
                    }
                    else
                    {
                        _logger?.LogWarning("AWS agent has too many errors, selecting coordinator");
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    }
                    break;

                case "azure-setup":
                    // Only select Azure agent if it hasn't had too many errors
                    var azureAgent = agents.FirstOrDefault(a => a.Name == "AzureAgent");
                    if (azureAgent != null && _agentErrorCount["AzureAgent"] < 3)
                    {
                        selectedAgent = azureAgent;
                    }
                    else
                    {
                        _logger?.LogWarning("Azure agent has too many errors, selecting coordinator");
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    }
                    break;

                case "integration":
                    // Alternate between AWS and Azure agents during integration
                    if (_lastAgentName == "AwsAgent")
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "AzureAgent");
                    else if (_lastAgentName == "AzureAgent")
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    else
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "AwsAgent");
                    break;

                case "monitoring":
                    selectedAgent = agents.FirstOrDefault(a => a.Name == "MonitorAgent") ??
                                  agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    break;

                case "completion":
                    // Only allow completion if no errors detected
                    if (!HasRecentErrors(history))
                    {
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                    }
                    else
                    {
                        _logger?.LogWarning("Errors detected, preventing premature completion");
                        selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
                        _inErrorRecoveryMode = true;
                    }
                    break;

                default:
                    // Smart selection based on conversation context
                    selectedAgent = SelectBasedOnContext(agents, history);
                    break;
            }
        }

        // Avoid selecting the same agent twice in a row (unless it's the only agent)
        if (selectedAgent != null && selectedAgent.Name == lastMessageAgent && agents.Count > 1)
        {
            // Find another suitable agent
            var otherAgents = agents.Where(a => a.Name != lastMessageAgent).ToList();
            if (otherAgents.Any())
            {
                selectedAgent = SelectBasedOnContext(otherAgents, history);
            }
        }

        // Fallback to coordinator if selection failed
        if (selectedAgent == null)
        {
            selectedAgent = agents.FirstOrDefault(a => a.Name == "CoordinatorAgent") ?? agents[0];
            _logger?.LogWarning("Could not select agent for phase {Phase}, using coordinator", currentPhase);
        }

        _lastAgentName = selectedAgent.Name;
        _agentParticipationCount[selectedAgent.Name ?? "Unknown"]++;

        _logger?.LogDebug("Selected {AgentName} for phase {Phase} (participation: {Count}, errors: {ErrorCount})",
            selectedAgent.Name, currentPhase,
            _agentParticipationCount[selectedAgent.Name ?? "Unknown"],
            _agentErrorCount[selectedAgent.Name ?? "Unknown"]);

        return await Task.FromResult(selectedAgent);
    }

    private void CheckForErrors(IReadOnlyList<ChatMessageContent> history)
    {
        if (!history.Any()) return;

        var lastMessage = history.Last();
        var content = lastMessage.Content?.ToLower() ?? string.Empty;

        // Check for error indicators
        var errorIndicators = new[] {
            "error", "exception", "failed", "unable to", "could not",
            "cannot", "denied", "unauthorized", "keynotfoundexception",
            "amazonserviceexception"
        };

        if (errorIndicators.Any(indicator => content.Contains(indicator)))
        {
            var agentName = lastMessage.AuthorName ?? "Unknown";
            if (_agentErrorCount.ContainsKey(agentName))
            {
                _agentErrorCount[agentName]++;
                _logger?.LogWarning("Error detected from {Agent}, error count: {Count}",
                    agentName, _agentErrorCount[agentName]);
            }

            // Enter error recovery mode if too many errors
            if (_agentErrorCount.Values.Sum() >= 5)
            {
                _inErrorRecoveryMode = true;
                _logger?.LogError("Too many errors detected, entering error recovery mode");
            }
        }
    }

    private bool HasRecentErrors(IReadOnlyList<ChatMessageContent> history)
    {
        var recentMessages = history.TakeLast(5).ToList();

        foreach (var message in recentMessages)
        {
            var content = message.Content?.ToLower() ?? string.Empty;

            if (content.Contains("error") || content.Contains("exception") ||
                content.Contains("failed") || content.Contains("unable to"))
            {
                // Make sure it's not a false positive
                if (!content.Contains("no error") && !content.Contains("without error") &&
                    !content.Contains("successful") && !content.Contains("completed"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Agent? SelectBasedOnContext(IReadOnlyList<Agent> agents, IReadOnlyList<ChatMessageContent> history)
    {
        if (!history.Any())
            return agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");

        var lastMessage = history.Last();
        var content = lastMessage.Content?.ToLower() ?? string.Empty;

        // If errors detected, prefer coordinator
        if (HasRecentErrors(history))
        {
            return agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
        }

        // If coordinator just created a plan, AWS should start implementing
        if (lastMessage.AuthorName == "CoordinatorAgent" &&
            (content.Contains("plan") || content.Contains("validated")))
        {
            return agents.FirstOrDefault(a => a.Name == "AwsAgent");
        }

        // If AWS just finished creating resources, Azure should configure
        if (lastMessage.AuthorName == "AwsAgent" &&
            (content.Contains("role arn") || content.Contains("sqs")))
        {
            return agents.FirstOrDefault(a => a.Name == "AzureAgent");
        }

        // If Azure configured connector, coordinator should verify and report
        if (lastMessage.AuthorName == "AzureAgent" &&
            (content.Contains("configured") || content.Contains("deployed")))
        {
            return agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
        }

        // Default to coordinator for orchestration
        return agents.FirstOrDefault(a => a.Name == "CoordinatorAgent");
    }

    private string DeterminePhase(IReadOnlyList<ChatMessageContent> history)
    {
        if (history.Count == 0)
        {
            return "initialization";
        }

        // Analyze recent messages to determine the current phase
        var recentMessages = history.TakeLast(5).ToList();
        var allContent = string.Join(" ", recentMessages.Select(m => m.Content?.ToLower() ?? string.Empty));

        // Check for errors first
        if (HasRecentErrors(history))
        {
            return "error-recovery";
        }

        // Check for specific phase indicators in order of progression
        if (allContent.Contains("final report") || allContent.Contains("setup complete"))
            return "completion";

        if (allContent.Contains("monitor") || allContent.Contains("verify") && allContent.Contains("ingestion"))
            return "monitoring";

        if (allContent.Contains("configure") && allContent.Contains("connector"))
            return "azure-setup";

        if (allContent.Contains("role arn") || allContent.Contains("sqs") || allContent.Contains("oidc"))
            return "aws-setup";

        if (allContent.Contains("plan") && allContent.Contains("setup"))
            return "planning";

        if (allContent.Contains("validate") || allContent.Contains("prerequisite"))
            return "validation";

        // Default phase based on message count
        if (history.Count < 3)
            return "initialization";
        else if (history.Count < 6)
            return "validation";
        else if (history.Count < 10)
            return "planning";
        else if (history.Count < 20)
            return "aws-setup";
        else if (history.Count < 30)
            return "azure-setup";
        else
            return "integration";
    }
}