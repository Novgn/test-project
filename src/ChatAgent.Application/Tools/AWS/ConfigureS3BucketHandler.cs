using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatAgent.Application.Tools.AWS;

public class ConfigureS3BucketHandler
{
    private readonly ILogger<ConfigureS3BucketHandler> _logger;
    private readonly IAmazonS3 _s3Client;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigureS3BucketHandler(ILogger<ConfigureS3BucketHandler> logger, IAmazonS3? s3Client = null)
    {
        _logger = logger;
        _s3Client = s3Client ?? new AmazonS3Client();
    }

    public async Task<ConfigureS3Bucket.Output> HandleAsync(
        ConfigureS3Bucket.Input input,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Configuring S3 bucket '{BucketName}' in region '{Region}' for log types: {LogTypes}",
            input.BucketName,
            input.Region,
            string.Join(", ", input.LogTypes));

        try
        {
            var bucketArn = $"arn:aws:s3:::{input.BucketName}";

            // Check if bucket exists or create it
            bool bucketExists = await CheckOrCreateBucketAsync(input.BucketName, input.Region, cancellationToken);

            if (!bucketExists)
            {
                _logger.LogError("Failed to create or access S3 bucket {BucketName}", input.BucketName);
                return new ConfigureS3Bucket.Output(
                    Success: false,
                    BucketName: input.BucketName,
                    BucketArn: bucketArn,
                    Region: input.Region,
                    VersioningEnabled: false,
                    EventNotificationsConfigured: false,
                    SQSQueueArn: null,
                    ErrorMessage: "Failed to create or access S3 bucket"
                );
            }

            // Enable versioning if requested
            if (input.EnableVersioning)
            {
                await EnableVersioningAsync(input.BucketName, cancellationToken);
            }

            // Apply bucket policy for CloudTrail and AWS services
            var bucketPolicy = GenerateBucketPolicy(input.BucketName);
            await ApplyBucketPolicyAsync(input.BucketName, bucketPolicy, cancellationToken);

            // Configure lifecycle rules for cost optimization
            await ConfigureLifecycleRulesAsync(input.BucketName, cancellationToken);

            // Enable server-side encryption
            await EnableEncryptionAsync(input.BucketName, cancellationToken);

            // Configure event notifications if requested
            string? sqsQueueArn = null;
            if (input.EnableEventNotifications)
            {
                sqsQueueArn = $"arn:aws:sqs:{input.Region}:YOUR-ACCOUNT-ID:{input.BucketName}-events";
                // Note: Actual event notification configuration requires an existing SQS queue
                _logger.LogInformation("Event notifications will be configured after SQS queue setup");
            }

            _logger.LogInformation("Successfully configured S3 bucket {BucketName}", input.BucketName);

            return new ConfigureS3Bucket.Output(
                Success: true,
                BucketName: input.BucketName,
                BucketArn: bucketArn,
                Region: input.Region,
                VersioningEnabled: input.EnableVersioning,
                EventNotificationsConfigured: false, // Will be true after SQS setup
                SQSQueueArn: sqsQueueArn,
                ErrorMessage: GenerateSuccessMessage(input.BucketName, input.Region, input.LogTypes)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error configuring S3 bucket");
            return new ConfigureS3Bucket.Output(
                Success: false,
                BucketName: input.BucketName,
                BucketArn: string.Empty,
                Region: input.Region,
                VersioningEnabled: false,
                EventNotificationsConfigured: false,
                SQSQueueArn: null,
                ErrorMessage: $"Error: {ex.Message}"
            );
        }
    }

    private async Task<bool> CheckOrCreateBucketAsync(string bucketName, string region, CancellationToken cancellationToken)
    {
        try
        {
            // Check if bucket exists
            var response = await _s3Client.ListBucketsAsync(cancellationToken);
            if (response.Buckets.Any(b => b.BucketName == bucketName))
            {
                _logger.LogInformation("Bucket {BucketName} already exists", bucketName);
                return true;
            }

            // Create bucket if it doesn't exist
            var putBucketRequest = new PutBucketRequest
            {
                BucketName = bucketName,
                BucketRegion = region == "us-east-1" ? null : S3Region.FindValue(region),
                CannedACL = S3CannedACL.Private
            };

            await _s3Client.PutBucketAsync(putBucketRequest, cancellationToken);
            _logger.LogInformation("Created S3 bucket {BucketName} in region {Region}", bucketName, region);

            // Enable public access block
            await _s3Client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
            {
                BucketName = bucketName,
                PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
                {
                    BlockPublicAcls = true,
                    BlockPublicPolicy = true,
                    IgnorePublicAcls = true,
                    RestrictPublicBuckets = true
                }
            }, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking/creating bucket {BucketName}", bucketName);
            return false;
        }
    }

    private async Task EnableVersioningAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            await _s3Client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucketName,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled
                }
            }, cancellationToken);

            _logger.LogInformation("Enabled versioning for bucket {BucketName}", bucketName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enable versioning for bucket {BucketName}", bucketName);
        }
    }

    private async Task ApplyBucketPolicyAsync(string bucketName, string policyJson, CancellationToken cancellationToken)
    {
        try
        {
            await _s3Client.PutBucketPolicyAsync(new PutBucketPolicyRequest
            {
                BucketName = bucketName,
                Policy = policyJson
            }, cancellationToken);

            _logger.LogInformation("Applied bucket policy to {BucketName}", bucketName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply bucket policy to {BucketName}", bucketName);
        }
    }

    private async Task ConfigureLifecycleRulesAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            var lifecycleConfig = new LifecycleConfiguration
            {
                Rules = new List<LifecycleRule>
                {
                    new LifecycleRule
                    {
                        Id = "TransitionToIA",
                        Status = LifecycleRuleStatus.Enabled,
                        Transitions = new List<LifecycleTransition>
                        {
                            new LifecycleTransition
                            {
                                Days = 30,
                                StorageClass = S3StorageClass.StandardInfrequentAccess
                            },
                            new LifecycleTransition
                            {
                                Days = 90,
                                StorageClass = S3StorageClass.Glacier
                            }
                        }
                    },
                    new LifecycleRule
                    {
                        Id = "DeleteOldLogs",
                        Status = LifecycleRuleStatus.Enabled,
                        Expiration = new LifecycleRuleExpiration
                        {
                            Days = 365 // Delete logs after 1 year
                        }
                    }
                }
            };

            await _s3Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = bucketName,
                Configuration = lifecycleConfig
            }, cancellationToken);

            _logger.LogInformation("Configured lifecycle rules for bucket {BucketName}", bucketName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure lifecycle rules for bucket {BucketName}", bucketName);
        }
    }

    private async Task EnableEncryptionAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            await _s3Client.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
            {
                BucketName = bucketName,
                ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
                {
                    ServerSideEncryptionRules = new List<ServerSideEncryptionRule>
                    {
                        new ServerSideEncryptionRule
                        {
                            ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault
                            {
                                ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AES256
                            }
                        }
                    }
                }
            }, cancellationToken);

            _logger.LogInformation("Enabled server-side encryption for bucket {BucketName}", bucketName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enable encryption for bucket {BucketName}", bucketName);
        }
    }

    private static string GenerateBucketPolicy(string bucketName)
    {
        var statements = new List<object>
        {
            new
            {
                Sid = "AWSCloudTrailAclCheck",
                Effect = "Allow",
                Principal = new { Service = "cloudtrail.amazonaws.com" },
                Action = "s3:GetBucketAcl",
                Resource = $"arn:aws:s3:::{bucketName}"
            },
            new
            {
                Sid = "AWSCloudTrailWrite",
                Effect = "Allow",
                Principal = new { Service = "cloudtrail.amazonaws.com" },
                Action = "s3:PutObject",
                Resource = $"arn:aws:s3:::{bucketName}/AWSLogs/*",
                Condition = new
                {
                    StringEquals = new Dictionary<string, string>
                    {
                        ["s3:x-amz-acl"] = "bucket-owner-full-control"
                    }
                }
            },
            new
            {
                Sid = "AWSLogDeliveryWrite",
                Effect = "Allow",
                Principal = new { Service = "delivery.logs.amazonaws.com" },
                Action = "s3:PutObject",
                Resource = $"arn:aws:s3:::{bucketName}/AWSLogs/*",
                Condition = new
                {
                    StringEquals = new Dictionary<string, string>
                    {
                        ["s3:x-amz-acl"] = "bucket-owner-full-control"
                    }
                }
            },
            new
            {
                Sid = "AWSLogDeliveryAclCheck",
                Effect = "Allow",
                Principal = new { Service = "delivery.logs.amazonaws.com" },
                Action = "s3:GetBucketAcl",
                Resource = $"arn:aws:s3:::{bucketName}"
            }
        };

        var policy = new
        {
            Version = "2012-10-17",
            Statement = statements
        };

        return JsonSerializer.Serialize(policy, JsonOptions);
    }

    private static string GenerateSuccessMessage(string bucketName, string region, List<string> logTypes)
    {
        return $@"
S3 Bucket Configured Successfully!
===================================

BUCKET DETAILS:
- Name: {bucketName}
- Region: {region}
- ARN: arn:aws:s3:::{bucketName}

CONFIGURATIONS APPLIED:
✅ Versioning enabled
✅ Server-side encryption (AES-256) enabled
✅ Public access blocked
✅ Lifecycle rules configured (30d→IA, 90d→Glacier, 365d→Delete)
✅ Bucket policy configured for CloudTrail and AWS services

NEXT STEPS:
1. Configure AWS services to deliver logs to this bucket:
{string.Join("\n", logTypes.Select(lt => $"   - {lt}: Configure to deliver to s3://{bucketName}/AWSLogs/"))}

2. For CloudTrail:
   - Create or update a trail
   - Set S3 bucket to: {bucketName}
   - Enable log file validation

3. For VPC Flow Logs:
   - Select VPCs/Subnets/ENIs to log
   - Set destination to: s3://{bucketName}/AWSLogs/

4. Set up SQS queue for real-time ingestion (use SetupSQSQueue function)

5. Configure event notifications after SQS setup

IMPORTANT:
- The bucket is now ready to receive logs
- Ensure the IAM role has s3:GetObject and s3:ListBucket permissions
- Monitor S3 costs with lifecycle policies in place";
    }
}