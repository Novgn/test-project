namespace ChatAgent.Application.Tools.Azure;

public static class FindConnectorSolution
{
    public sealed record Input(
        string ConnectorName,
        string SubscriptionId,
        string ResourceGroupName,
        string WorkspaceName
    );

    public sealed record Output(
        string SolutionId,
        string SolutionName,
        string Description,
        string Version,
        bool IsInstalled
    );
}