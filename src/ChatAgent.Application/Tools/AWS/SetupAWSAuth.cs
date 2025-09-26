namespace ChatAgent.Application.Tools.AWS;

/// <summary>
/// Defines the input and output contracts for AWS authentication setup.
/// This is used to configure OIDC provider and IAM roles for Microsoft Sentinel integration.
/// </summary>
public static class SetupAWSAuth
{
    /// <summary>
    /// Input parameters for AWS authentication setup
    /// </summary>
    /// <param name="AwsAccessKeyId">AWS Access Key ID for authentication</param>
    /// <param name="AwsSecretAccessKey">AWS Secret Access Key for authentication</param>
    /// <param name="AwsRegion">AWS region where resources will be created</param>
    /// <param name="WorkspaceId">Microsoft Sentinel Workspace ID (used as External ID)</param>
    public record Input(
        string AwsAccessKeyId,
        string AwsSecretAccessKey,
        string AwsRegion,
        string WorkspaceId
    );

    /// <summary>
    /// Output containing the result of the authentication setup
    /// </summary>
    /// <param name="Result">Setup result message including Role ARN and next steps</param>
    public record Output(string Result);
}