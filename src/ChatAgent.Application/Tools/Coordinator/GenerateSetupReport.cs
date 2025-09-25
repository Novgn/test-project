namespace ChatAgent.Application.Tools.Coordinator;

public static class GenerateSetupReport
{
    public sealed class Input
    {
        public string? SetupDetails { get; init; }
    }

    public sealed class Output
    {
        public bool Success { get; init; }
        public string Report { get; init; } = string.Empty;
        public string? SetupDetails { get; init; }
        public DateTime Timestamp { get; init; }
    }
}