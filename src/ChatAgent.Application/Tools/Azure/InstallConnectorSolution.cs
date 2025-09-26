namespace ChatAgent.Application.Tools.Azure;

public static class InstallConnectorSolution
{
    public sealed record Input(
        string SolutionId,
        string SubscriptionId,
        string ResourceGroupName,
        string WorkspaceName,
        string Version = "latest",
        bool EnableDataConnectors = true,
        Dictionary<string, object>? Parameters = null
    );

    public sealed record Output(
        bool Success,
        string OperationId,
        string Status,
        string Message,
        List<string> InstalledComponents,
        List<string> EnabledDataConnectors,
        Dictionary<string, string>? ConfigurationRequired
    );
}