namespace ChatAgent.Application.Tools.Azure;

public static class CheckInstallationStatus
{
    public sealed record Input(
        string OperationId,
        string SolutionId,
        string SubscriptionId,
        string ResourceGroupName,
        string WorkspaceName
    );

    public sealed record Output(
        string Status,
        int PercentComplete,
        string CurrentStep,
        List<string> CompletedSteps,
        string? ErrorMessage,
        DateTime? EstimatedCompletionTime
    );
}