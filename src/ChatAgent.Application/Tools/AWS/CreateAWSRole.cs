namespace ChatAgent.Application.Tools.AWS;

public static class CreateAWSRole
{
    public sealed record Input(
        string RoleName,
        string ExternalId,
        string TrustedEntity,
        List<string> Policies,
        string Description = "Role for Microsoft Sentinel AWS connector"
    );

    public sealed record Output(
        bool Success,
        string RoleArn,
        string RoleName,
        string ExternalId,
        string TrustPolicy,
        List<string> AttachedPolicies,
        string? ErrorMessage
    );
}