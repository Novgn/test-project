using Amazon.CloudTrail;
using Amazon.CloudTrail.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using static ChatAgent.Application.Tools.AWS.SetupAWSInfra;

namespace ChatAgent.Application.Tools.AWS;

/// <summary>
/// Handler for setting up AWS infrastructure for Microsoft Sentinel log ingestion.
/// Creates and configures S3 bucket, SQS queue, and optionally CloudTrail.
/// This handler must be run AFTER SetupAWSAuthHandler to ensure IAM role exists.
/// </summary>
public class SetupAWSInfraHandler : IToolHandler<Input, Output>
{
    public string Name => "data_setup-aws-infra";
    public string Description => "Sets up AWS infrastructure (S3 bucket, SQS queue) for Microsoft Sentinel log ingestion.";

    private readonly ILogger<SetupAWSInfraHandler> _logger;
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonSQS _sqsClient;
    private readonly IAmazonCloudTrail _cloudTrailClient;

    public SetupAWSInfraHandler(
        ILogger<SetupAWSInfraHandler> logger,
        IAmazonS3 s3Client,
        IAmazonSQS sqsClient,
        IAmazonCloudTrail cloudTrailClient)
    {
        _logger = logger;
        _s3Client = s3Client;
        _sqsClient = sqsClient;
        _cloudTrailClient = cloudTrailClient;
    }

    public async Task<Output> HandleAsync(Input input, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting AWS infrastructure setup for workspace: {WorkspaceId}", input.WorkspaceId);

        var configuredServices = new List<string>();

        // Create S3 Bucket
        var bucketName = input.BucketName ?? $"sentinel-logs-{input.WorkspaceId[..8]}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await CreateS3BucketAsync(bucketName, input.AwsRegion, cancellationToken);

        // Add bucket tags
        await AddBucketTagsAsync(bucketName, input.WorkspaceId, cancellationToken);

        // Create SQS Queue
        var queueName = input.QueueName ?? $"sentinel-queue-{input.WorkspaceId[..8]}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var queueUrl = await CreateSQSQueueAsync(queueName, cancellationToken);
        var queueArn = await GetQueueArnAsync(queueUrl, cancellationToken);

        // Add queue tags
        await AddQueueTagsAsync(queueUrl, input.WorkspaceId, cancellationToken);

        // Configure S3 bucket policy to allow AWS services and role to access logs
        await ConfigureS3BucketPolicyAsync(bucketName, input.RoleArn, cancellationToken);

        // Configure S3 event notifications to send to SQS
        await ConfigureS3EventNotificationsAsync(bucketName, queueArn, cancellationToken);

        // Configure SQS queue policy to allow S3 to send messages
        await ConfigureSQSQueuePolicyAsync(queueUrl, queueArn, bucketName, cancellationToken);

        // Set up CloudTrail log delivery if enabled
        string? cloudTrailArn = null;
        if (input.EnableCloudTrail)
        {
            cloudTrailArn = await SetupCloudTrailAsync(
                bucketName,
                input.CloudTrailName,
                input.KmsKeyId,
                input.IsMultiRegionTrail,
                input.IsOrganizationTrail,
                input.EnableDataEvents,
                input.EnableLogFileValidation,
                cancellationToken);

            if (!string.IsNullOrEmpty(cloudTrailArn))
            {
                configuredServices.Add("CloudTrail");
                if (input.IsMultiRegionTrail)
                    configuredServices.Add("Multi-Region Trail");
                if (input.IsOrganizationTrail)
                    configuredServices.Add("Organization Trail");
                if (input.EnableDataEvents)
                    configuredServices.Add("S3 Data Events");
            }
        }

        var bucketArn = $"arn:aws:s3:::{bucketName}";

        _logger.LogInformation("AWS infrastructure setup completed successfully");

        return new Output(
            BucketName: bucketName,
            BucketArn: bucketArn,
            QueueUrl: queueUrl,
            QueueArn: queueArn,
            CloudTrailArn: cloudTrailArn,
            KmsKeyArn: input.KmsKeyId,
            Result: $"Infrastructure setup completed. S3 bucket: {bucketName}, SQS queue: {queueUrl}",
            ConfiguredServices: configuredServices
        );
    }

    private async Task CreateS3BucketAsync(string bucketName, string region, CancellationToken cancellationToken)
    {
        try
        {
            var putBucketRequest = new PutBucketRequest
            {
                BucketName = bucketName,
                BucketRegion = S3Region.FindValue(region)
            };

            await _s3Client.PutBucketAsync(putBucketRequest, cancellationToken);
            _logger.LogInformation("Created S3 bucket: {BucketName}", bucketName);

            // Enable versioning on the bucket
            await _s3Client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucketName,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled
                }
            }, cancellationToken);

            // Enable server-side encryption by default
            await _s3Client.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
            {
                BucketName = bucketName,
                ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
                {
                    ServerSideEncryptionRules =
                    [
                        new ServerSideEncryptionRule
                        {
                            ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault
                            {
                                ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AES256
                            }
                        }
                    ]
                }
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyExists" || ex.ErrorCode == "BucketAlreadyOwnedByYou")
        {
            _logger.LogInformation("S3 bucket already exists: {BucketName}", bucketName);
        }
    }

    private async Task AddBucketTagsAsync(string bucketName, string workspaceId, CancellationToken cancellationToken)
    {
        var tags = new List<Amazon.S3.Model.Tag>
        {
            new() { Key = "Purpose", Value = "MicrosoftSentinel" },
            new() { Key = "WorkspaceId", Value = workspaceId },
            new() { Key = "CreatedBy", Value = "SentinelConnector" },
            new() { Key = "CreatedAt", Value = DateTime.UtcNow.ToString("yyyy-MM-dd") }
        };

        await _s3Client.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucketName,
            TagSet = tags
        }, cancellationToken);

        _logger.LogInformation("Added tags to S3 bucket: {BucketName}", bucketName);
    }

    private async Task<string> CreateSQSQueueAsync(string queueName, CancellationToken cancellationToken)
    {
        var createQueueRequest = new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                { "MessageRetentionPeriod", "1209600" }, // 14 days (maximum)
                { "VisibilityTimeout", "300" }, // 5 minutes
                { "ReceiveMessageWaitTimeSeconds", "20" } // Long polling
            }
        };

        var createQueueResponse = await _sqsClient.CreateQueueAsync(createQueueRequest, cancellationToken);
        _logger.LogInformation("Created SQS queue: {QueueUrl}", createQueueResponse.QueueUrl);

        return createQueueResponse.QueueUrl;
    }

    private async Task<string> GetQueueArnAsync(string queueUrl, CancellationToken cancellationToken)
    {
        var attributes = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["QueueArn"]
        }, cancellationToken);

        return attributes.Attributes["QueueArn"];
    }

    private async Task AddQueueTagsAsync(string queueUrl, string workspaceId, CancellationToken cancellationToken)
    {
        var tags = new Dictionary<string, string>
        {
            { "Purpose", "MicrosoftSentinel" },
            { "WorkspaceId", workspaceId },
            { "CreatedBy", "SentinelConnector" },
            { "CreatedAt", DateTime.UtcNow.ToString("yyyy-MM-dd") }
        };

        await _sqsClient.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = queueUrl,
            Tags = tags
        }, cancellationToken);

        _logger.LogInformation("Added tags to SQS queue: {QueueUrl}", queueUrl);
    }

    private async Task ConfigureS3BucketPolicyAsync(string bucketName, string roleArn, CancellationToken cancellationToken)
    {
        var bucketPolicy = new
        {
            Version = "2012-10-17",
            Statement = new object[]
            {
                new
                {
                    Sid = "AllowArnReadAccessS3Bucket",
                    Effect = "Allow",
                    Principal = new { AWS = roleArn },
                    Action = "s3:GetObject",
                    Resource = $"arn:aws:s3:::{bucketName}/*"
                },
                new
                {
                    Sid = "AllowArnListS3Bucket",
                    Effect = "Allow",
                    Principal = new { AWS = roleArn },
                    Action = "s3:ListBucket",
                    Resource = $"arn:aws:s3:::{bucketName}"
                },
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
                    Resource = $"arn:aws:s3:::{bucketName}/*",
                    Condition = new
                    {
                        StringEquals = new Dictionary<string, string>
                        {
                            { "s3:x-amz-acl", "bucket-owner-full-control" }
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
                },
                new
                {
                    Sid = "AWSLogDeliveryWrite",
                    Effect = "Allow",
                    Principal = new { Service = "delivery.logs.amazonaws.com" },
                    Action = "s3:PutObject",
                    Resource = $"arn:aws:s3:::{bucketName}/*",
                    Condition = new
                    {
                        StringEquals = new Dictionary<string, string>
                        {
                            { "s3:x-amz-acl", "bucket-owner-full-control" }
                        }
                    }
                }
            }
        };

        await _s3Client.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = bucketName,
            Policy = JsonSerializer.Serialize(bucketPolicy)
        }, cancellationToken);

        _logger.LogInformation("Configured S3 bucket policy for: {BucketName}", bucketName);
    }

    private async Task ConfigureS3EventNotificationsAsync(string bucketName, string queueArn, CancellationToken cancellationToken)
    {
        var notificationConfiguration = new PutBucketNotificationRequest
        {
            BucketName = bucketName,
            QueueConfigurations =
            [
                new QueueConfiguration
                {
                    Id = "SentinelNotification",
                    Queue = queueArn,
                    Events = [EventType.ObjectCreatedAll]
                }
            ]
        };

        await _s3Client.PutBucketNotificationAsync(notificationConfiguration, cancellationToken);
        _logger.LogInformation("Configured S3 event notifications for bucket: {BucketName}", bucketName);
    }

    private async Task ConfigureSQSQueuePolicyAsync(string queueUrl, string queueArn, string bucketName, CancellationToken cancellationToken)
    {
        var queuePolicy = new
        {
            Version = "2012-10-17",
            Statement = new object[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { Service = "s3.amazonaws.com" },
                    Action = "sqs:SendMessage",
                    Resource = queueArn,
                    Condition = new
                    {
                        ArnLike = new Dictionary<string, string>
                        {
                            { "aws:SourceArn", $"arn:aws:s3:::{bucketName}" }
                        }
                    }
                }
            }
        };

        await _sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string>
            {
                { "Policy", JsonSerializer.Serialize(queuePolicy) }
            }
        }, cancellationToken);

        _logger.LogInformation("Configured SQS queue policy for: {QueueUrl}", queueUrl);
    }

    private async Task ConfigureCloudTrailDataEventsAsync(string trailName, CancellationToken cancellationToken)
    {
        try
        {
            var eventSelector = new EventSelector
            {
                ReadWriteType = ReadWriteType.All,
                IncludeManagementEvents = true,
                DataResources = new List<DataResource>
                {
                    new DataResource
                    {
                        Type = "AWS::S3::Object",
                        Values = new List<string> { "arn:aws:s3:::*/*" }
                    }
                }
            };

            await _cloudTrailClient.PutEventSelectorsAsync(new PutEventSelectorsRequest
            {
                TrailName = trailName,
                EventSelectors = new List<EventSelector> { eventSelector }
            }, cancellationToken);

            _logger.LogInformation("Configured CloudTrail data events for trail: {TrailName}", trailName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not configure CloudTrail data events: {Message}", ex.Message);
        }
    }

    private async Task<string?> SetupCloudTrailAsync(
        string bucketName,
        string? trailName,
        string? kmsKeyId,
        bool isMultiRegion,
        bool isOrganization,
        bool enableDataEvents,
        bool enableLogFileValidation,
        CancellationToken cancellationToken)
    {
        try
        {
            trailName ??= $"sentinel-trail-{bucketName[..8]}";

            // Check if trail already exists
            var trails = await _cloudTrailClient.ListTrailsAsync(new ListTrailsRequest(), cancellationToken);
            if (trails.Trails.Any(t => t.Name == trailName))
            {
                _logger.LogInformation("CloudTrail already exists: {TrailName}", trailName);
                return null; // Trail already exists, return null
            }

            var createTrailRequest = new CreateTrailRequest
            {
                Name = trailName,
                S3BucketName = bucketName,
                IsMultiRegionTrail = isMultiRegion,
                IncludeGlobalServiceEvents = isMultiRegion,
                IsOrganizationTrail = isOrganization,
                EnableLogFileValidation = enableLogFileValidation
            };

            // Add KMS encryption if specified
            if (!string.IsNullOrEmpty(kmsKeyId))
            {
                createTrailRequest.KmsKeyId = kmsKeyId;
            }

            var createTrailResponse = await _cloudTrailClient.CreateTrailAsync(createTrailRequest, cancellationToken);
            _logger.LogInformation("Created CloudTrail: {TrailName}", trailName);

            // Start logging
            await _cloudTrailClient.StartLoggingAsync(new StartLoggingRequest
            {
                Name = createTrailResponse.TrailARN
            }, cancellationToken);

            // Configure data events if requested
            if (enableDataEvents)
            {
                await ConfigureCloudTrailDataEventsAsync(trailName, cancellationToken);
            }

            _logger.LogInformation("Started CloudTrail logging for: {TrailName}", trailName);
            return createTrailResponse.TrailARN;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not set up CloudTrail: {Message}", ex.Message);
            return null;
        }
    }

}