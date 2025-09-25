using ChatAgent.Application.Tools.Azure;

namespace ChatAgent.Application.Plugins.Azure;

public sealed class AzureToolHandlers
{
    public FindConnectorSolutionHandler FindConnectorSolutionHandler { get; }

    public AzureToolHandlers(
        FindConnectorSolutionHandler findConnectorSolutionHandler
    )
    {
        FindConnectorSolutionHandler = findConnectorSolutionHandler;
    }
}