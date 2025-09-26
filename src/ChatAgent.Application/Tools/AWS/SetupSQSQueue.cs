namespace ChatAgent.Application.Tools.AWS;

public static class SetupSQSQueue
{
    public sealed record Input(
        string QueueName,
        string Region,
        string S3BucketArn,
        Dictionary<string, string>? QueueAttributes = null,
        int MessageRetentionPeriod = 14,
        int VisibilityTimeout = 300
    );

    public sealed record Output(
        bool Success,
        string QueueUrl,
        string QueueArn,
        string Region,
        bool S3EventsConfigured,
        Dictionary<string, string> QueueAttributes,
        string? ErrorMessage
    );
}