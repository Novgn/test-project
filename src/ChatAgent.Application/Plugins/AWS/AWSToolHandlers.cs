using ChatAgent.Application.Tools.AWS;

namespace ChatAgent.Application.Plugins.AWS;

public sealed class AWSToolHandlers
{
    public CreateAWSRoleHandler CreateAWSRoleHandler { get; }
    public ConfigureS3BucketHandler ConfigureS3BucketHandler { get; }
    public SetupSQSQueueHandler SetupSQSQueueHandler { get; }

    public AWSToolHandlers(
        CreateAWSRoleHandler createAWSRoleHandler,
        ConfigureS3BucketHandler configureS3BucketHandler,
        SetupSQSQueueHandler setupSQSQueueHandler)
    {
        CreateAWSRoleHandler = createAWSRoleHandler;
        ConfigureS3BucketHandler = configureS3BucketHandler;
        SetupSQSQueueHandler = setupSQSQueueHandler;
    }
}