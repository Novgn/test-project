using Amazon;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatAgent.Application.Tools.AWS;

public class CreateAWSRoleHandler
{
    private readonly ILogger<CreateAWSRoleHandler> _logger;
    private readonly IAmazonIdentityManagementService _iamClient;

    // Microsoft Sentinel's AWS Account ID for cross-account access
    private const string MicrosoftSentinelAccountId = "197857026523";

    public CreateAWSRoleHandler(ILogger<CreateAWSRoleHandler> logger, IAmazonIdentityManagementService? iamClient = null)
    {
        _logger = logger;
        _iamClient = iamClient ?? new AmazonIdentityManagementServiceClient();
    }

    public async Task<CreateAWSRole.Output> HandleAsync(
        CreateAWSRole.Input input,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating AWS IAM role '{RoleName}' with external ID '{ExternalId}'",
            input.RoleName,
            input.ExternalId);

        try
        {
            // Generate trust policy for Microsoft Sentinel
            var trustPolicy = GenerateTrustPolicy(input.ExternalId);

            // Create the IAM role
            var createRoleRequest = new CreateRoleRequest
            {
                RoleName = input.RoleName,
                AssumeRolePolicyDocument = trustPolicy,
                Description = input.Description ?? $"Role for Microsoft Sentinel AWS connector (Created by ChatAgent)",
                MaxSessionDuration = 3600, // 1 hour
                Tags = new List<Tag>
                {
                    new() { Key = "Purpose", Value = "MicrosoftSentinel" },
                    new() { Key = "Connector", Value = "AWS" },
                    new() { Key = "CreatedBy", Value = "ChatAgent" },
                    new() { Key = "ExternalId", Value = input.ExternalId }
                }
            };

            var createRoleResponse = await _iamClient.CreateRoleAsync(createRoleRequest, cancellationToken);
            var roleArn = createRoleResponse.Role.Arn;

            _logger.LogInformation("Successfully created IAM role with ARN: {RoleArn}", roleArn);

            // Attach required policies based on input
            var attachedPolicies = new List<string>();
            foreach (var policyType in input.Policies)
            {
                var policyArn = GetPolicyArnForType(policyType);
                if (!string.IsNullOrEmpty(policyArn))
                {
                    try
                    {
                        await _iamClient.AttachRolePolicyAsync(new AttachRolePolicyRequest
                        {
                            RoleName = input.RoleName,
                            PolicyArn = policyArn
                        }, cancellationToken);

                        attachedPolicies.Add($"{policyType} ({policyArn})");
                        _logger.LogInformation("Attached policy {PolicyType} to role", policyType);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to attach policy {PolicyType}", policyType);
                    }
                }
            }

            // Create custom inline policy if needed for specific log types
            if (input.Policies.Any(p => p.Contains("Custom", StringComparison.OrdinalIgnoreCase)))
            {
                var inlinePolicy = GenerateCustomInlinePolicy(input.Policies);
                await _iamClient.PutRolePolicyAsync(new PutRolePolicyRequest
                {
                    RoleName = input.RoleName,
                    PolicyName = "MicrosoftSentinelCustomAccess",
                    PolicyDocument = inlinePolicy
                }, cancellationToken);

                attachedPolicies.Add("Custom inline policy for Sentinel access");
            }

            return new CreateAWSRole.Output(
                Success: true,
                RoleArn: roleArn,
                RoleName: input.RoleName,
                ExternalId: input.ExternalId,
                TrustPolicy: trustPolicy,
                AttachedPolicies: attachedPolicies,
                ErrorMessage: GenerateNextSteps(roleArn, input.ExternalId)
            );
        }
        catch (EntityAlreadyExistsException)
        {
            _logger.LogWarning("IAM role {RoleName} already exists", input.RoleName);

            // Get existing role details
            try
            {
                var getRoleResponse = await _iamClient.GetRoleAsync(new GetRoleRequest
                {
                    RoleName = input.RoleName
                }, cancellationToken);

                return new CreateAWSRole.Output(
                    Success: true,
                    RoleArn: getRoleResponse.Role.Arn,
                    RoleName: input.RoleName,
                    ExternalId: input.ExternalId,
                    TrustPolicy: getRoleResponse.Role.AssumeRolePolicyDocument,
                    AttachedPolicies: new List<string> { "Role already exists - policies not modified" },
                    ErrorMessage: $"Role already exists. Using existing role.\n{GenerateNextSteps(getRoleResponse.Role.Arn, input.ExternalId)}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get existing role details");
                return new CreateAWSRole.Output(
                    Success: false,
                    RoleArn: string.Empty,
                    RoleName: input.RoleName,
                    ExternalId: input.ExternalId,
                    TrustPolicy: string.Empty,
                    AttachedPolicies: new List<string>(),
                    ErrorMessage: $"Role exists but cannot retrieve details: {ex.Message}"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating IAM role");
            return new CreateAWSRole.Output(
                Success: false,
                RoleArn: string.Empty,
                RoleName: input.RoleName,
                ExternalId: input.ExternalId,
                TrustPolicy: string.Empty,
                AttachedPolicies: new List<string>(),
                ErrorMessage: $"Error: {ex.Message}"
            );
        }
    }

    private static string GenerateTrustPolicy(string externalId)
    {
        var policy = new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new
                    {
                        AWS = $"arn:aws:iam::{MicrosoftSentinelAccountId}:root"
                    },
                    Action = "sts:AssumeRole",
                    Condition = new
                    {
                        StringEquals = new Dictionary<string, string>
                        {
                            ["sts:ExternalId"] = externalId
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(policy, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static string GetPolicyArnForType(string policyType)
    {
        return policyType.ToLowerInvariant() switch
        {
            "cloudtrail" or "awscloudtrail" => "arn:aws:iam::aws:policy/AWSCloudTrail_ReadOnlyAccess",
            "s3" or "s3readonly" => "arn:aws:iam::aws:policy/AmazonS3ReadOnlyAccess",
            "guardduty" => "arn:aws:iam::aws:policy/AmazonGuardDutyReadOnlyAccess",
            "securityhub" => "arn:aws:iam::aws:policy/SecurityAudit",
            "vpc" or "vpcflowlogs" => "arn:aws:iam::aws:policy/AmazonVPCReadOnlyAccess",
            "cloudwatch" => "arn:aws:iam::aws:policy/CloudWatchLogsReadOnlyAccess",
            _ => string.Empty
        };
    }

    private static string GenerateCustomInlinePolicy(List<string> logTypes)
    {
        var statements = new List<object>
        {
            // S3 permissions for log buckets
            new
            {
                Effect = "Allow",
                Action = new[]
                {
                    "s3:GetObject",
                    "s3:ListBucket",
                    "s3:GetBucketLocation",
                    "s3:GetObjectVersion",
                    "s3:GetBucketPolicy"
                },
                Resource = new[]
                {
                    "arn:aws:s3:::*-cloudtrail-*",
                    "arn:aws:s3:::*-cloudtrail-*/*",
                    "arn:aws:s3:::*-awslogs-*",
                    "arn:aws:s3:::*-awslogs-*/*"
                }
            },
            // SQS permissions for event notifications
            new
            {
                Effect = "Allow",
                Action = new[]
                {
                    "sqs:DeleteMessage",
                    "sqs:ReceiveMessage",
                    "sqs:GetQueueAttributes",
                    "sqs:GetQueueUrl"
                },
                Resource = "arn:aws:sqs:*:*:*-sentinel-*"
            }
        };

        var policy = new
        {
            Version = "2012-10-17",
            Statement = statements
        };

        return JsonSerializer.Serialize(policy, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static string GenerateNextSteps(string roleArn, string externalId)
    {
        return $@"
IAM Role Created Successfully!
==============================

ROLE DETAILS:
- Role ARN: {roleArn}
- External ID: {externalId}
- Trust Relationship: Microsoft Sentinel Account ({MicrosoftSentinelAccountId})

NEXT STEPS:
1. Note down the Role ARN and External ID
2. In Microsoft Sentinel:
   - Navigate to Data Connectors
   - Search for 'Amazon Web Services'
   - Click 'Open connector page'
   - Enter the Role ARN: {roleArn}
   - Enter the External ID: {externalId}
   - Click 'Add AWS Account'

3. Configure S3 buckets and SQS queues for log collection
4. Verify data ingestion in Log Analytics workspace

IMPORTANT:
- Keep the External ID secure
- The role trusts only Microsoft Sentinel's AWS account
- Review and adjust policies as needed for your security requirements";
    }
}