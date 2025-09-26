using System.ComponentModel;
using ChatAgent.Application.Tools.AWS;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ChatAgent.Application.Plugins.AWS;

public class AWSPlugin
{
    private readonly ILogger<AWSPlugin> _logger;
    private readonly AWSToolHandlers _toolHandlers;

    public AWSPlugin(ILogger<AWSPlugin> logger, AWSToolHandlers toolHandlers)
    {
        _logger = logger;
        _toolHandlers = toolHandlers;
    }

    [KernelFunction("SetupAWSAuth")]
    [Description("Set up AWS authentication with OIDC provider and IAM role for Microsoft Sentinel")]
    public async Task<SetupAWSAuth.Output> SetupAWSAuthAsync(
        [Description("AWS Access Key ID")] string awsAccessKeyId,
        [Description("AWS Secret Access Key")] string awsSecretAccessKey,
        [Description("AWS Region (e.g., 'us-east-1')")] string awsRegion,
        [Description("Microsoft Sentinel Workspace ID")] string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var input = new SetupAWSAuth.Input(
            AwsAccessKeyId: awsAccessKeyId,
            AwsSecretAccessKey: awsSecretAccessKey,
            AwsRegion: awsRegion,
            WorkspaceId: workspaceId
        );

        _logger.LogInformation("Setting up AWS authentication for workspace: {WorkspaceId}", workspaceId);
        return await _toolHandlers.SetupAWSAuthHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("SetupAWSInfra")]
    [Description("Set up AWS infrastructure (S3, SQS, CloudTrail) for Microsoft Sentinel log ingestion")]
    public async Task<SetupAWSInfra.Output> SetupAWSInfraAsync(
        [Description("AWS Region (e.g., 'us-east-1')")] string awsRegion,
        [Description("Microsoft Sentinel Workspace ID")] string workspaceId,
        [Description("IAM Role ARN for access")] string roleArn,
        [Description("Custom S3 bucket name (optional)")] string? bucketName = null,
        [Description("Custom SQS queue name (optional)")] string? queueName = null,
        [Description("CloudTrail name (optional)")] string? cloudTrailName = null,
        [Description("Enable CloudTrail logging")] bool enableCloudTrail = true,
        [Description("Enable KMS encryption for CloudTrail")] bool enableKmsEncryption = false,
        [Description("KMS Key ID for encryption (optional)")] string? kmsKeyId = null,
        [Description("Create multi-region trail")] bool isMultiRegionTrail = true,
        [Description("Create organization-wide trail")] bool isOrganizationTrail = false,
        [Description("Enable S3 data events logging")] bool enableDataEvents = false,
        [Description("Enable CloudTrail log file validation")] bool enableLogFileValidation = true,
        CancellationToken cancellationToken = default)
    {
        var input = new SetupAWSInfra.Input(
            AwsRegion: awsRegion,
            WorkspaceId: workspaceId,
            RoleArn: roleArn,
            BucketName: bucketName,
            QueueName: queueName,
            CloudTrailName: cloudTrailName,
            EnableCloudTrail: enableCloudTrail,
            EnableKmsEncryption: enableKmsEncryption,
            KmsKeyId: kmsKeyId,
            IsMultiRegionTrail: isMultiRegionTrail,
            IsOrganizationTrail: isOrganizationTrail,
            EnableDataEvents: enableDataEvents,
            EnableLogFileValidation: enableLogFileValidation
        );

        _logger.LogInformation("Setting up AWS infrastructure for workspace: {WorkspaceId}", workspaceId);
        return await _toolHandlers.SetupAWSInfraHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("GenerateAWSSetupSummary")]
    [Description("Generate a summary of the AWS setup for Microsoft Sentinel connector")]
    public Task<string> GenerateAWSSetupSummaryAsync(
        [Description("IAM Role ARN")] string roleArn,
        [Description("S3 Bucket name")] string bucketName,
        [Description("SQS Queue URL")] string queueUrl,
        [Description("External ID (Workspace ID)")] string workspaceId,
        [Description("AWS Region")] string region,
        [Description("CloudTrail ARN (optional)")] string? cloudTrailArn = null,
        [Description("Configured services list")] List<string>? configuredServices = null,
        CancellationToken cancellationToken = default)
    {
        var summary = $@"
AWS Setup Summary for Microsoft Sentinel Connector
===================================================

CONFIGURED RESOURCES:

✅ IAM Role:
   - ARN: {roleArn}
   - External ID: {workspaceId}
   - OIDC Provider: Configured for Microsoft Sentinel

✅ S3 Bucket:
   - Name: {bucketName}
   - Region: {region}
   - Purpose: Log storage for AWS service logs
   - Policies: Configured for CloudTrail and role access

✅ SQS Queue:
   - URL: {queueUrl}
   - Region: {region}
   - Purpose: Real-time notifications for new logs
   - Event Notifications: Configured from S3
";

        if (!string.IsNullOrEmpty(cloudTrailArn))
        {
            summary += $@"
✅ CloudTrail:
   - ARN: {cloudTrailArn}
   - Status: Logging enabled
";
        }

        if (configuredServices?.Any() == true)
        {
            summary += $@"
✅ Configured Services:
{string.Join("\n", configuredServices.Select(s => $"   - {s}"))}
";
        }

        summary += $@"

NEXT STEPS IN MICROSOFT SENTINEL:
1. Navigate to Data Connectors
2. Search for 'Amazon Web Services S3'
3. Click 'Open connector page'
4. Under 'Add connection':
   - Role ARN: {roleArn}
   - SQS URL: {queueUrl}
   - Select destination table: AWSCloudTrail
5. Click 'Add connection'

VERIFY CONNECTION:
- Check connector status in Sentinel
- Query: AWSCloudTrail | take 10
- Monitor SQS queue for messages
- Check S3 bucket for CloudTrail logs
";

        _logger.LogInformation("Generated AWS setup summary");
        return Task.FromResult(summary);
    }
}