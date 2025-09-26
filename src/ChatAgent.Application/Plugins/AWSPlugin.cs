using System.ComponentModel;
using ChatAgent.Application.Plugins.AWS;
using ChatAgent.Application.Tools.AWS;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace ChatAgent.Application.Plugins;

public class AWSPlugin
{
    private readonly ILogger<AWSPlugin> _logger;
    private readonly AWSToolHandlers _toolHandlers;

    public AWSPlugin(ILogger<AWSPlugin> logger, AWSToolHandlers toolHandlers)
    {
        _logger = logger;
        _toolHandlers = toolHandlers;
    }

    [KernelFunction("CreateAWSRole")]
    [Description("Create an IAM role for Microsoft Sentinel to access AWS resources")]
    public async Task<CreateAWSRole.Output> CreateAWSRoleAsync(
        [Description("Name for the IAM role (e.g., 'MicrosoftSentinelRole')")] string roleName,
        [Description("External ID for secure cross-account access")] string externalId,
        [Description("List of AWS services to access (e.g., 'CloudTrail', 'GuardDuty', 'VPCFlowLogs')")] List<string> policies,
        [Description("Optional description for the role")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        var input = new CreateAWSRole.Input(
            RoleName: roleName,
            ExternalId: externalId,
            TrustedEntity: "197857026523", // Microsoft Sentinel's AWS Account ID
            Policies: policies,
            Description: description ?? "Role for Microsoft Sentinel AWS connector"
        );

        _logger.LogInformation("Creating AWS IAM role: {RoleName}", roleName);
        return await _toolHandlers.CreateAWSRoleHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("ConfigureS3Bucket")]
    [Description("Configure an S3 bucket for AWS log collection by Microsoft Sentinel")]
    public async Task<ConfigureS3Bucket.Output> ConfigureS3BucketAsync(
        [Description("Name of the S3 bucket")] string bucketName,
        [Description("AWS region (e.g., 'us-east-1')")] string region,
        [Description("Types of logs to collect (e.g., 'CloudTrail', 'VPCFlowLogs', 'GuardDuty')")] List<string> logTypes,
        [Description("Enable versioning on the bucket")] bool enableVersioning = true,
        [Description("Enable event notifications to SQS")] bool enableEventNotifications = true,
        CancellationToken cancellationToken = default)
    {
        var input = new ConfigureS3Bucket.Input(
            BucketName: bucketName,
            Region: region,
            LogTypes: logTypes,
            BucketPolicy: null,
            EnableVersioning: enableVersioning,
            EnableEventNotifications: enableEventNotifications
        );

        _logger.LogInformation("Configuring S3 bucket: {BucketName} in {Region}", bucketName, region);
        return await _toolHandlers.ConfigureS3BucketHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("SetupSQSQueue")]
    [Description("Set up an SQS queue for S3 event notifications for real-time log ingestion")]
    public async Task<SetupSQSQueue.Output> SetupSQSQueueAsync(
        [Description("Name for the SQS queue")] string queueName,
        [Description("AWS region (e.g., 'us-east-1')")] string region,
        [Description("ARN of the S3 bucket sending notifications")] string s3BucketArn,
        [Description("Message retention period in days (1-14)")] int messageRetentionPeriod = 14,
        [Description("Visibility timeout in seconds")] int visibilityTimeout = 300,
        CancellationToken cancellationToken = default)
    {
        var input = new SetupSQSQueue.Input(
            QueueName: queueName,
            Region: region,
            S3BucketArn: s3BucketArn,
            QueueAttributes: null,
            MessageRetentionPeriod: messageRetentionPeriod,
            VisibilityTimeout: visibilityTimeout
        );

        _logger.LogInformation("Setting up SQS queue: {QueueName} in {Region}", queueName, region);
        return await _toolHandlers.SetupSQSQueueHandler.HandleAsync(input, cancellationToken);
    }

    [KernelFunction("GenerateAWSSetupSummary")]
    [Description("Generate a summary of the AWS setup for Microsoft Sentinel connector")]
    public Task<string> GenerateAWSSetupSummaryAsync(
        [Description("IAM Role ARN")] string? roleArn = null,
        [Description("S3 Bucket name")] string? bucketName = null,
        [Description("SQS Queue URL")] string? queueUrl = null,
        [Description("External ID used")] string? externalId = null,
        [Description("AWS Region")] string? region = null,
        CancellationToken cancellationToken = default)
    {
        var summary = @"
AWS Setup Summary for Microsoft Sentinel Connector
===================================================

CONFIGURED RESOURCES:
";

        if (!string.IsNullOrEmpty(roleArn))
            summary += $@"
✅ IAM Role:
   - ARN: {roleArn}
   - External ID: {externalId ?? "Not specified"}
   - Trust Relationship: Microsoft Sentinel Account (197857026523)
";

        if (!string.IsNullOrEmpty(bucketName))
            summary += $@"
✅ S3 Bucket:
   - Name: {bucketName}
   - Region: {region ?? "Not specified"}
   - Purpose: Log storage for CloudTrail/VPC/GuardDuty logs
";

        if (!string.IsNullOrEmpty(queueUrl))
            summary += $@"
✅ SQS Queue:
   - URL: {queueUrl}
   - Region: {region ?? "Not specified"}
   - Purpose: Real-time notifications for new logs
";

        summary += @"

NEXT STEPS:
1. Complete any manual AWS configurations if needed
2. In Microsoft Sentinel:
   - Navigate to Data Connectors
   - Search for 'Amazon Web Services'
   - Click 'Open connector page'
   - Enter the Role ARN and External ID
   - Add SQS URLs for real-time ingestion
   - Click 'Connect'

3. Verify data ingestion:
   - Check connector status
   - Query AWSCloudTrail table in Log Analytics
   - Set up analytics rules and workbooks

TROUBLESHOOTING:
- Ensure IAM role trust policy is correct
- Verify S3 bucket has logs
- Check SQS queue for messages
- Review CloudWatch logs for errors
";

        _logger.LogInformation("Generated AWS setup summary");
        return Task.FromResult(summary);
    }
}