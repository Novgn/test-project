using ChatAgent.Application.Tools.AWS;

namespace ChatAgent.Application.Plugins.AWS;

public sealed class AWSToolHandlers
{
    public SetupAWSAuthHandler SetupAWSAuthHandler { get; }
    public SetupAWSInfraHandler SetupAWSInfraHandler { get; }

    public AWSToolHandlers(
        SetupAWSAuthHandler setupAWSAuthHandler,
        SetupAWSInfraHandler setupAWSInfraHandler)
    {
        SetupAWSAuthHandler = setupAWSAuthHandler;
        SetupAWSInfraHandler = setupAWSInfraHandler;
    }
}