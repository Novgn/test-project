namespace ChatAgent.Application.Tools.Coordinator;

public static class ValidatePrerequisites
{
    public sealed class Input
    {
        public required string SubscriptionId { get; init; }
        public required string TenantId { get; init; }
        public required string WorkspaceId { get; init; }
    }

    public sealed class Output
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public Details? Details { get; init; }
    }

    public sealed class Details
    {
        public string SubscriptionId { get; init; } = string.Empty;
        public string TenantId { get; init; } = string.Empty;
        public string WorkspaceId { get; init; } = string.Empty;
        public string AzureCredentials { get; init; } = string.Empty;
        public string AwsCredentials { get; init; } = string.Empty;
    }
}