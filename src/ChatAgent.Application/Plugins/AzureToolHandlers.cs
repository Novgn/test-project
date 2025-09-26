using ChatAgent.Application.Tools.Azure;

namespace ChatAgent.Application.Plugins.Azure;

public sealed class AzureToolHandlers
{
    public FindConnectorSolutionHandler FindConnectorSolutionHandler { get; }
    public InstallConnectorSolutionHandler InstallConnectorSolutionHandler { get; }

    public AzureToolHandlers(
        FindConnectorSolutionHandler findConnectorSolutionHandler,
        InstallConnectorSolutionHandler installConnectorSolutionHandler
    )
    {
        FindConnectorSolutionHandler = findConnectorSolutionHandler;
        InstallConnectorSolutionHandler = installConnectorSolutionHandler;
    }
}