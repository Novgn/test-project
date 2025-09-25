namespace ChatAgent.Application.Tools.Coordinator;

public static class PlanConnectorSetup
{
    public sealed class Input
    {
        public required string LogTypes { get; init; }
        public required string AwsRegion { get; init; }
    }

    public sealed class Output
    {
        public bool Success { get; init; }
        public string[] Plan { get; init; } = Array.Empty<string>();
        public string LogTypes { get; init; } = string.Empty;
        public string AwsRegion { get; init; } = string.Empty;
    }
}