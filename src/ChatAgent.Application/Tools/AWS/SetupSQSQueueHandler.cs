using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatAgent.Application.Tools.AWS;

public class SetupSQSQueueHandler
{
    private readonly ILogger<SetupSQSQueueHandler> _logger;
    private readonly IAmazonSQS _sqsClient;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SetupSQSQueueHandler(ILogger<SetupSQSQueueHandler> logger, IAmazonSQS? sqsClient = null)
    {
        _logger = logger;
        _sqsClient = sqsClient ?? new AmazonSQSClient();
    }

    public async Task<SetupSQSQueue.Output> HandleAsync(
        SetupSQSQueue.Input input,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Setting up SQS queue '{QueueName}' in region '{Region}'",
            input.QueueName,
            input.Region);

        try
        {
            // Create the SQS queue
            var createQueueRequest = new CreateQueueRequest
            {
                QueueName = input.QueueName,
                Attributes = new Dictionary<string, string>
                {
                    ["MessageRetentionPeriod"] = (input.MessageRetentionPeriod * 24 * 3600).ToString(),
                    ["VisibilityTimeout"] = input.VisibilityTimeout.ToString(),
                    ["ReceiveMessageWaitTimeSeconds"] = "20", // Enable long polling
                    ["DelaySeconds"] = "0"
                }
            };

            var createResponse = await _sqsClient.CreateQueueAsync(createQueueRequest, cancellationToken);
            var queueUrl = createResponse.QueueUrl;

            _logger.LogInformation("Created SQS queue with URL: {QueueUrl}", queueUrl);

            // Get queue attributes to get the ARN
            var getAttributesResponse = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            }, cancellationToken);

            var queueArn = getAttributesResponse.Attributes["QueueArn"];

            // Set queue policy to allow S3 to send messages
            var queuePolicy = GenerateQueuePolicy(queueArn, input.S3BucketArn);
            await _sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["Policy"] = queuePolicy
                }
            }, cancellationToken);

            _logger.LogInformation("Applied queue policy for S3 access");

            // Configure dead letter queue if needed
            await ConfigureDeadLetterQueueAsync(queueUrl, input.QueueName, cancellationToken);

            // Add tags for better organization
            await _sqsClient.TagQueueAsync(new TagQueueRequest
            {
                QueueUrl = queueUrl,
                Tags = new Dictionary<string, string>
                {
                    ["Purpose"] = "MicrosoftSentinel",
                    ["LogIngestion"] = "S3Events",
                    ["CreatedBy"] = "ChatAgent"
                }
            }, cancellationToken);

            _logger.LogInformation("Successfully configured SQS queue {QueueName}", input.QueueName);

            return new SetupSQSQueue.Output(
                Success: true,
                QueueUrl: queueUrl,
                QueueArn: queueArn,
                Region: input.Region,
                S3EventsConfigured: true,
                QueueAttributes: new Dictionary<string, string>
                {
                    ["MessageRetentionPeriod"] = (input.MessageRetentionPeriod * 24 * 3600).ToString(),
                    ["VisibilityTimeout"] = input.VisibilityTimeout.ToString(),
                    ["ReceiveMessageWaitTimeSeconds"] = "20"
                },
                ErrorMessage: GenerateSuccessMessage(queueUrl, queueArn, input.S3BucketArn, input.Region)
            );
        }
        catch (QueueNameExistsException)
        {
            _logger.LogWarning("SQS queue {QueueName} already exists", input.QueueName);

            try
            {
                // Get existing queue URL
                var getUrlResponse = await _sqsClient.GetQueueUrlAsync(input.QueueName, cancellationToken);
                var queueUrl = getUrlResponse.QueueUrl;

                // Get queue ARN
                var getAttributesResponse = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = queueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                }, cancellationToken);

                var queueArn = getAttributesResponse.Attributes["QueueArn"];

                return new SetupSQSQueue.Output(
                    Success: true,
                    QueueUrl: queueUrl,
                    QueueArn: queueArn,
                    Region: input.Region,
                    S3EventsConfigured: true,
                    QueueAttributes: new Dictionary<string, string>(),
                    ErrorMessage: $"Queue already exists. Using existing queue: {queueUrl}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get existing queue details");
                return new SetupSQSQueue.Output(
                    Success: false,
                    QueueUrl: string.Empty,
                    QueueArn: string.Empty,
                    Region: input.Region,
                    S3EventsConfigured: false,
                    QueueAttributes: new Dictionary<string, string>(),
                    ErrorMessage: $"Queue exists but cannot retrieve details: {ex.Message}"
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting up SQS queue");
            return new SetupSQSQueue.Output(
                Success: false,
                QueueUrl: string.Empty,
                QueueArn: string.Empty,
                Region: input.Region,
                S3EventsConfigured: false,
                QueueAttributes: new Dictionary<string, string>(),
                ErrorMessage: $"Error: {ex.Message}"
            );
        }
    }

    private async Task ConfigureDeadLetterQueueAsync(string mainQueueUrl, string mainQueueName, CancellationToken cancellationToken)
    {
        try
        {
            // Create DLQ
            var dlqName = $"{mainQueueName}-dlq";
            var createDlqRequest = new CreateQueueRequest
            {
                QueueName = dlqName,
                Attributes = new Dictionary<string, string>
                {
                    ["MessageRetentionPeriod"] = (14 * 24 * 3600).ToString() // 14 days
                }
            };

            var dlqResponse = await _sqsClient.CreateQueueAsync(createDlqRequest, cancellationToken);

            // Get DLQ ARN
            var dlqAttributesResponse = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = dlqResponse.QueueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            }, cancellationToken);

            var dlqArn = dlqAttributesResponse.Attributes["QueueArn"];

            // Configure redrive policy on main queue
            var redrivePolicy = new
            {
                deadLetterTargetArn = dlqArn,
                maxReceiveCount = 3 // Move to DLQ after 3 failed processing attempts
            };

            await _sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = mainQueueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["RedrivePolicy"] = JsonSerializer.Serialize(redrivePolicy, JsonOptions)
                }
            }, cancellationToken);

            _logger.LogInformation("Configured dead letter queue for {QueueName}", mainQueueName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure dead letter queue");
        }
    }

    private static string GenerateQueuePolicy(string queueArn, string s3BucketArn)
    {
        var policy = new
        {
            Version = "2012-10-17",
            Statement = new[]
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
                            ["aws:SourceArn"] = s3BucketArn
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(policy, JsonOptions);
    }

    private static string GenerateSuccessMessage(string queueUrl, string queueArn, string s3BucketArn, string region)
    {
        var bucketName = s3BucketArn.Replace("arn:aws:s3:::", "");

        return $@"
SQS Queue Created Successfully!
================================

QUEUE DETAILS:
- URL: {queueUrl}
- ARN: {queueArn}
- Region: {region}

CONFIGURATIONS APPLIED:
✅ Long polling enabled (20 seconds)
✅ Message retention: 14 days
✅ Visibility timeout: 300 seconds
✅ Dead letter queue configured
✅ Queue policy configured for S3 access

NEXT STEPS:
1. Configure S3 Event Notifications:
   - Go to your S3 bucket ({bucketName})
   - Navigate to Properties → Event notifications
   - Create notification:
     * Name: sentinel-logs
     * Event types: All object create events
     * Prefix: AWSLogs/
     * Destination: SQS queue
     * SQS queue ARN: {queueArn}

2. Test the Configuration:
   - Upload a test file to the S3 bucket
   - Check SQS queue for message (should appear within seconds)
   - Message should contain S3 event details

3. In Microsoft Sentinel:
   - Configure AWS S3 connector
   - Add SQS URL: {queueUrl}
   - Enable real-time ingestion

4. Monitor Queue Health:
   - Set up CloudWatch alarms for:
     * ApproximateNumberOfMessagesVisible
     * ApproximateAgeOfOldestMessage
     * NumberOfMessagesSent
   - Monitor DLQ for failed messages

IMPORTANT:
- Ensure IAM role has necessary SQS permissions
- Queue will start receiving messages after S3 event notification setup
- Monitor costs - consider adjusting retention period if needed";
    }
}