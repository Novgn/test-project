using ChatAgent.Application.Tools.Coordinator;

namespace ChatAgent.Application.Plugins.Coordinator;

public sealed class CoordinatorToolHandlers
{
    public ValidatePrerequisitesHandler ValidatePrerequisitesHandler { get; }
    public PlanConnectorSetupHandler PlanConnectorSetupHandler { get; }
    public GenerateSetupReportHandler GenerateSetupReportHandler { get; }

    public CoordinatorToolHandlers(
        ValidatePrerequisitesHandler validatePrerequisitesHandler,
        PlanConnectorSetupHandler planConnectorSetupHandler,
        GenerateSetupReportHandler generateSetupReportHandler)
    {
        ValidatePrerequisitesHandler = validatePrerequisitesHandler;
        PlanConnectorSetupHandler = planConnectorSetupHandler;
        GenerateSetupReportHandler = generateSetupReportHandler;
    }
}