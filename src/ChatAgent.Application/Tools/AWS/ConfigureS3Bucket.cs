namespace ChatAgent.Application.Tools.AWS;

public static class ConfigureS3Bucket
{
    public sealed record Input(
        string BucketName,
        string Region,
        List<string> LogTypes,
        string? BucketPolicy = null,
        bool EnableVersioning = true,
        bool EnableEventNotifications = true
    );

    public sealed record Output(
        bool Success,
        string BucketName,
        string BucketArn,
        string Region,
        bool VersioningEnabled,
        bool EventNotificationsConfigured,
        string? SQSQueueArn,
        string? ErrorMessage
    );
}