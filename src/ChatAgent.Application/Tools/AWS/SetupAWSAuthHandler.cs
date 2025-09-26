using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.SecurityToken;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using static ChatAgent.Application.Tools.AWS.SetupAWSAuth;

namespace ChatAgent.Application.Tools.AWS;

public class SetupAWSAuthHandler : IToolHandler<Input, Output>
{
    public string Name => "data_setup-aws-auth";
    public string Description => "Sets up AWS authentication for Microsoft Sentinel integration.";

    private readonly ILogger<SetupAWSAuthHandler> _logger;
    private readonly IAmazonIdentityManagementService _iamClient;

    private readonly IAmazonSecurityTokenService _stsClient;

    public SetupAWSAuthHandler(ILogger<SetupAWSAuthHandler> logger, IAmazonIdentityManagementService? iamClient = null, IAmazonSecurityTokenService? stsClient = null)
    {
        _logger = logger;
        _iamClient = iamClient ?? new AmazonIdentityManagementServiceClient();
        _stsClient = stsClient ?? new AmazonSecurityTokenServiceClient();
    }

    public async Task<Output> HandleAsync(Input input, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting AWS authentication setup for workspace: {WorkspaceId}", input.WorkspaceId);

        // Get current account ID
        var callerIdentity = await _stsClient.GetCallerIdentityAsync(new Amazon.SecurityToken.Model.GetCallerIdentityRequest(), cancellationToken);
        var accountId = callerIdentity.Account;
        _logger.LogInformation("Current AWS Account ID: {AccountId}", accountId);

        // Create OIDC Provider if it doesn't exist
        var oidcProviderArn = await CreateOidcProviderAsync(input.WorkspaceId, cancellationToken);

        // Create IAM role for Microsoft Sentinel
        var roleName = $"MicrosoftSentinel-{input.WorkspaceId[..8]}";
        var roleArn = await CreateArnRoleAsync(roleName, input.WorkspaceId, oidcProviderArn, cancellationToken);

        // Attach necessary policies to the role
        await AttachPoliciesToRoleAsync(roleName, cancellationToken);

        // Generate next steps for user
        var nextSteps = GenerateNextSteps(roleArn);

        _logger.LogInformation("AWS authentication setup completed successfully");

        return new Output($"AWS authentication setup completed successfully.\n\nRole ARN: {roleArn}\n\n{nextSteps}");
    }

    /// <summary>
    /// Creates or updates OIDC provider for Microsoft Sentinel authentication.
    /// Uses Azure AD endpoint for web identity federation.
    /// </summary>
    /// <param name="externalId">Workspace ID to add as audience</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>OIDC Provider ARN</returns>
    private async Task<string> CreateOidcProviderAsync(string externalId, CancellationToken cancellationToken)
    {
        var providerUrl = "https://sts.windows.net/33e01921-4d64-4f8c-a055-5bdaffd5e33d/";

        // Check if OIDC provider already exists
        var listResponse = await _iamClient.ListOpenIDConnectProvidersAsync(new ListOpenIDConnectProvidersRequest(), cancellationToken);
        foreach (var provider in listResponse.OpenIDConnectProviderList)
        {
            var getResponse = await _iamClient.GetOpenIDConnectProviderAsync(new GetOpenIDConnectProviderRequest
            {
                OpenIDConnectProviderArn = provider.Arn
            }, cancellationToken);

            if (getResponse.Url == providerUrl)
            {
                // Provider exists, check if our workspace ID is in the audience
                if (!getResponse.ClientIDList.Contains(externalId))
                {
                    // Add the workspace ID to the existing provider
                    await _iamClient.AddClientIDToOpenIDConnectProviderAsync(new AddClientIDToOpenIDConnectProviderRequest
                    {
                        OpenIDConnectProviderArn = provider.Arn,
                        ClientID = externalId
                    }, cancellationToken);
                    _logger.LogInformation("Added workspace ID {WorkspaceId} to existing OIDC Provider: {ProviderArn}", externalId, provider.Arn);
                }
                else
                {
                    _logger.LogInformation("Found existing OIDC Provider with workspace ID already configured: {ProviderArn}", provider.Arn);
                }
                return provider.Arn;
            }
        }

        // Create new OIDC provider
        var thumbprints = new List<string> { "626d44e704d1ceabe3bf0d53397464ac8080142c" };
        var createProviderRequest = new CreateOpenIDConnectProviderRequest
        {
            Url = providerUrl,
            ClientIDList = [externalId],
            ThumbprintList = thumbprints
        };

        var createProviderResponse = await _iamClient.CreateOpenIDConnectProviderAsync(createProviderRequest, cancellationToken);
        _logger.LogInformation("Created new OIDC Provider: {ProviderArn}", createProviderResponse.OpenIDConnectProviderArn);

        return createProviderResponse.OpenIDConnectProviderArn;
    }

    private async Task<string> CreateArnRoleAsync(string roleName, string externalId, string oidcProviderArn, CancellationToken cancellationToken)
    {
        var assumeRolePolicy = new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new
                    {
                        Federated = oidcProviderArn
                    },
                    Action = "sts:AssumeRoleWithWebIdentity",
                    Condition = new
                    {
                        StringEquals = new Dictionary<string, string>
                        {
                            { "sts.windows.net:aud", externalId }
                        }
                    }
                }
            }
        };

        var createRoleRequest = new CreateRoleRequest
        {
            RoleName = roleName,
            AssumeRolePolicyDocument = JsonSerializer.Serialize(assumeRolePolicy),
            Description = "Role for Microsoft Sentinel AWS connector"
        };

        var createRoleResponse = await _iamClient.CreateRoleAsync(createRoleRequest, cancellationToken);
        _logger.LogInformation("Created IAM Role: {RoleName} with ARN: {RoleArn}", roleName, createRoleResponse.Role.Arn);

        return createRoleResponse.Role.Arn;
    }

    private async Task AttachPoliciesToRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        // Attach AWS managed policy for S3 read access
        var s3Policy = new AttachRolePolicyRequest
        {
            RoleName = roleName,
            PolicyArn = "arn:aws:iam::aws:policy/AmazonS3ReadOnlyAccess"
        };
        await _iamClient.AttachRolePolicyAsync(s3Policy, cancellationToken);
        _logger.LogInformation("Attached S3 read-only policy to role {RoleName}", roleName);

        // Attach AWS managed policy for SQS access
        var sqsPolicy = new AttachRolePolicyRequest
        {
            RoleName = roleName,
            PolicyArn = "arn:aws:iam::aws:policy/AmazonSQSReadOnlyAccess"
        };
        await _iamClient.AttachRolePolicyAsync(sqsPolicy, cancellationToken);
        _logger.LogInformation("Attached SQS read-only policy to role {RoleName}", roleName);

        // For CloudTrail logs, attach the CloudTrail read-only policy
        var cloudTrailPolicy = new AttachRolePolicyRequest
        {
            RoleName = roleName,
            PolicyArn = "arn:aws:iam::aws:policy/AWSCloudTrailReadOnlyAccess"
        };
        await _iamClient.AttachRolePolicyAsync(cloudTrailPolicy, cancellationToken);
        _logger.LogInformation("Attached CloudTrail read-only policy to role {RoleName}", roleName);
    }

    private static string GenerateNextSteps(string roleArn)
    {
        return $@"Next Steps:
1. Navigate to Microsoft Sentinel > Data connectors > Amazon Web Services S3
2. Open the connector page
3. Under 'Add connection':
   - Paste the Role ARN: {roleArn}
   - Configure your S3 bucket and SQS queue settings
   - Select the appropriate destination table for your AWS service logs
4. Click 'Add connection' to complete the setup

Note: Make sure your S3 bucket and SQS queue are properly configured to receive logs from your AWS services.";
    }
}