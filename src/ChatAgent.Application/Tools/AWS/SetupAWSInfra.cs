namespace ChatAgent.Application.Tools.AWS;

/// <summary>
/// Defines the input and output contracts for AWS infrastructure setup.
/// This includes S3 bucket, SQS queue, and optionally CloudTrail configuration.
/// Must be run AFTER SetupAWSAuth to ensure IAM role exists.
/// </summary>
public static class SetupAWSInfra
{
    /// <summary>
    /// Input parameters for AWS infrastructure setup
    /// </summary>
    /// <param name="AwsRegion">AWS region for resource creation</param>
    /// <param name="WorkspaceId">Microsoft Sentinel Workspace ID</param>
    /// <param name="RoleArn">IAM Role ARN created by SetupAWSAuth</param>
    /// <param name="BucketName">Optional custom S3 bucket name</param>
    /// <param name="QueueName">Optional custom SQS queue name</param>
    /// <param name="CloudTrailName">Optional custom CloudTrail name</param>
    /// <param name="EnableCloudTrail">Whether to create CloudTrail</param>
    /// <param name="EnableKmsEncryption">Enable KMS encryption for CloudTrail logs</param>
    /// <param name="KmsKeyId">KMS Key ID for encryption (if enabled)</param>
    /// <param name="IsMultiRegionTrail">Create multi-region trail for complete visibility</param>
    /// <param name="IsOrganizationTrail">Create organization-wide trail for all accounts</param>
    /// <param name="EnableDataEvents">Enable S3 data events logging (can be costly)</param>
    /// <param name="EnableLogFileValidation">Enable log file integrity validation</param>
    public record Input(
        string AwsRegion,
        string WorkspaceId,
        string RoleArn,
        string? BucketName = null,
        string? QueueName = null,
        string? CloudTrailName = null,
        bool EnableCloudTrail = false,
        bool EnableKmsEncryption = false,
        string? KmsKeyId = null,
        bool IsMultiRegionTrail = true,
        bool IsOrganizationTrail = false,
        bool EnableDataEvents = false,
        bool EnableLogFileValidation = true
    );

    /// <summary>
    /// Output containing details of created infrastructure
    /// </summary>
    /// <param name="BucketName">Name of the created/configured S3 bucket</param>
    /// <param name="BucketArn">ARN of the S3 bucket</param>
    /// <param name="QueueUrl">URL of the created SQS queue</param>
    /// <param name="QueueArn">ARN of the SQS queue</param>
    /// <param name="CloudTrailArn">ARN of CloudTrail (if created)</param>
    /// <param name="KmsKeyArn">ARN of KMS key (if used)</param>
    /// <param name="Result">Summary message of the setup</param>
    /// <param name="ConfiguredServices">List of services that were configured</param>
    public record Output(
        string BucketName,
        string BucketArn,
        string QueueUrl,
        string QueueArn,
        string? CloudTrailArn,
        string? KmsKeyArn,
        string Result,
        List<string> ConfiguredServices
    );
}