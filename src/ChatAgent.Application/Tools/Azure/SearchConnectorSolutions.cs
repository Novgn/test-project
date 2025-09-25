namespace ChatAgent.Application.Tools.Azure;

public static class SearchConnectorSolutions
{
    public sealed record Input(
        string ConnectorType,
        string SubscriptionId,
        string ResourceGroupName,
        string WorkspaceName
    );

    public sealed record SolutionInfo(
        string SolutionId,
        string SolutionName,
        string Publisher,
        string Description,
        string Version,
        bool IsInstalled,
        string ContentKind,
        List<string> DataConnectorKinds
    );

    public sealed record Output(
        List<SolutionInfo> AvailableSolutions,
        List<SolutionInfo> InstalledSolutions,
        string SearchQuery,
        int TotalCount
    );
}