using System.ComponentModel;
using ChatAgent.Application.Plugins.Coordinator;
using ChatAgent.Application.Tools.Coordinator;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ChatAgent.Application.Plugins;

public class CoordinatorPlugin
{
    private readonly ILogger<CoordinatorPlugin> _logger;
    private readonly CoordinatorToolHandlers _toolHandlers;

    public CoordinatorPlugin(ILogger<CoordinatorPlugin> logger, CoordinatorToolHandlers toolHandlers)
    {
        _logger = logger;
        _toolHandlers = toolHandlers;
    }

    [KernelFunction("ValidatePrerequisites")]
    [Description("Validate all prerequisites for Sentinel connector setup")]
    public async Task<ValidatePrerequisites.Output> ValidatePrerequisitesAsync(
        [Description("Azure subscription ID")] string subscriptionId,
        [Description("Azure tenant ID")] string tenantId,
        [Description("Sentinel workspace ID")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var input = new ValidatePrerequisites.Input
        {
            SubscriptionId = subscriptionId,
            TenantId = tenantId,
            WorkspaceId = workspaceId
        };

        _logger.LogInformation("Validating prerequisites for subscription: {SubscriptionId}", subscriptionId);
        return await _toolHandlers.ValidatePrerequisitesHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("PlanConnectorSetup")]
    [Description("Create a comprehensive setup plan for the connector")]
    public async Task<PlanConnectorSetup.Output> PlanConnectorSetupAsync(
        [Description("Log types to collect")] string logTypes,
        [Description("AWS region")] string awsRegion,
        CancellationToken cancellationToken = default)
    {
        var input = new PlanConnectorSetup.Input
        {
            LogTypes = logTypes,
            AwsRegion = awsRegion
        };

        _logger.LogInformation("Creating setup plan for log types: {LogTypes} in region: {Region}",
            logTypes, awsRegion);
        return await _toolHandlers.PlanConnectorSetupHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("GenerateSetupReport")]
    [Description("Generate a final setup report")]
    public async Task<GenerateSetupReport.Output> GenerateSetupReportAsync(
        [Description("Setup details JSON")] string? setupDetails = null,
        CancellationToken cancellationToken = default)
    {
        var input = new GenerateSetupReport.Input
        {
            SetupDetails = setupDetails
        };

        _logger.LogInformation("Generating setup report");
        return await _toolHandlers.GenerateSetupReportHandler.HandleAsync(input, cancellationToken);
    }
}