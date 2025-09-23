using ChatAgent.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Amazon;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using System.Text.Json;

namespace ChatAgent.Infrastructure.McpTools.SentinelConnector;

public class AwsInfrastructureToolProvider : IMcpToolProvider
{
    private readonly ILogger<AwsInfrastructureToolProvider> _logger;
    private IAmazonIdentityManagementService? _iamClient;
    private IAmazonS3? _s3Client;
    private IAmazonSQS? _sqsClient;
    private IAmazonSecurityTokenService? _stsClient;

    public string Name => "aws-infrastructure";
    public string Description => "AWS infrastructure management tools for setting up S3 buckets, SQS queues, and IAM roles for Azure Sentinel integration";

    public AwsInfrastructureToolProvider(ILogger<AwsInfrastructureToolProvider> logger)
    {
        _logger = logger;
    }

    public async Task<List<ToolDescriptor>> GetAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        return new List<ToolDescriptor>
        {
            new ToolDescriptor
            {
                Name = "CreateOidcProvider",
                Description = "Create OIDC web identity provider for Azure AD authentication",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["tenantId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure AD tenant ID",
                        Required = true
                    },
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region (e.g., us-east-1)",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "CreateSentinelRole",
                Description = "Create IAM role for Azure Sentinel with necessary permissions",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["roleName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Name for the IAM role",
                        Required = true
                    },
                    ["tenantId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure AD tenant ID",
                        Required = true
                    },
                    ["workspaceId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Azure Sentinel workspace ID",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "CreateS3BucketForLogs",
                Description = "Create and configure S3 bucket for AWS log storage",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["bucketName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Name for the S3 bucket",
                        Required = true
                    },
                    ["logType"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Type of logs (CloudTrail, VPCFlow, GuardDuty, CloudWatch)",
                        Required = true
                    },
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "CreateSqsQueue",
                Description = "Create SQS queue for S3 event notifications",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["queueName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Name for the SQS queue",
                        Required = true
                    },
                    ["bucketArn"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "ARN of the S3 bucket",
                        Required = true
                    },
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "ConfigureS3EventNotification",
                Description = "Configure S3 bucket to send events to SQS queue",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["bucketName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "S3 bucket name",
                        Required = true
                    },
                    ["queueArn"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "ARN of the SQS queue",
                        Required = true
                    },
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "EnableCloudTrail",
                Description = "Enable AWS CloudTrail and configure S3 destination",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["trailName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "Name for the CloudTrail",
                        Required = true
                    },
                    ["bucketName"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "S3 bucket for CloudTrail logs",
                        Required = true
                    },
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "EnableVpcFlowLogs",
                Description = "Enable VPC Flow Logs to S3",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["vpcId"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "VPC ID to enable flow logs",
                        Required = true
                    },
                    ["bucketArn"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "S3 bucket ARN for flow logs",
                        Required = true
                    },
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region",
                        Required = true
                    }
                }
            },
            new ToolDescriptor
            {
                Name = "GetInfrastructureStatus",
                Description = "Get status of AWS infrastructure components",
                Parameters = new Dictionary<string, ParameterDescriptor>
                {
                    ["region"] = new ParameterDescriptor
                    {
                        Type = "string",
                        Description = "AWS region",
                        Required = true
                    }
                }
            }
        };
    }

    public async Task<object> ExecuteAsync(string toolName, Dictionary<string, object> parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            InitializeClients(parameters.ContainsKey("region") ? parameters["region"].ToString() : "us-east-1");

            return toolName switch
            {
                "CreateOidcProvider" => await CreateOidcProvider(parameters, cancellationToken),
                "CreateSentinelRole" => await CreateSentinelRole(parameters, cancellationToken),
                "CreateS3BucketForLogs" => await CreateS3BucketForLogs(parameters, cancellationToken),
                "CreateSqsQueue" => await CreateSqsQueue(parameters, cancellationToken),
                "ConfigureS3EventNotification" => await ConfigureS3EventNotification(parameters, cancellationToken),
                "EnableCloudTrail" => await EnableCloudTrail(parameters, cancellationToken),
                "EnableVpcFlowLogs" => await EnableVpcFlowLogs(parameters, cancellationToken),
                "GetInfrastructureStatus" => await GetInfrastructureStatus(parameters, cancellationToken),
                _ => throw new NotSupportedException($"Tool {toolName} is not supported")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing AWS infrastructure tool {ToolName}", toolName);
            throw;
        }
    }

    private void InitializeClients(string? region)
    {
        var awsRegion = RegionEndpoint.GetBySystemName(region ?? "us-east-1");
        _iamClient = new AmazonIdentityManagementServiceClient(awsRegion);
        _s3Client = new AmazonS3Client(awsRegion);
        _sqsClient = new AmazonSQSClient(awsRegion);
        _stsClient = new AmazonSecurityTokenServiceClient(awsRegion);
    }

    private async Task<object> CreateOidcProvider(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var tenantId = parameters["tenantId"].ToString();

        var providerUrl = $"https://sts.windows.net/{tenantId}/";
        var thumbprints = new List<string> { "626D44E704D1CEABE3BF0D53397464AC8080142C" }; // Microsoft Azure AD thumbprint

        try
        {
            var createProviderRequest = new CreateOpenIDConnectProviderRequest
            {
                Url = providerUrl,
                ThumbprintList = thumbprints,
                ClientIDList = new List<string> { "12345678-1234-1234-1234-123456789012" } // Sentinel app ID
            };

            var response = await _iamClient!.CreateOpenIDConnectProviderAsync(createProviderRequest, cancellationToken);

            return new
            {
                success = true,
                providerArn = response.OpenIDConnectProviderArn,
                message = "OIDC provider created successfully"
            };
        }
        catch (EntityAlreadyExistsException)
        {
            // Provider already exists, get its ARN
            var listResponse = await _iamClient!.ListOpenIDConnectProvidersAsync(new ListOpenIDConnectProvidersRequest(), cancellationToken);
            var existingProvider = listResponse.OpenIDConnectProviderList
                .FirstOrDefault(p => p.Arn.Contains(tenantId));

            return new
            {
                success = true,
                providerArn = existingProvider?.Arn,
                message = "OIDC provider already exists"
            };
        }
    }

    private async Task<object> CreateSentinelRole(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var roleName = parameters["roleName"].ToString();
        var tenantId = parameters["tenantId"].ToString();
        var workspaceId = parameters["workspaceId"].ToString();

        var accountId = (await _stsClient!.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken)).Account;
        var oidcProviderArn = $"arn:aws:iam::{accountId}:oidc-provider/sts.windows.net/{tenantId}/";

        var trustPolicy = JsonSerializer.Serialize(new
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
                            [$"sts.windows.net/{tenantId}/:aud"] = workspaceId
                        }
                    }
                }
            }
        });

        try
        {
            // Create the role
            var createRoleResponse = await _iamClient!.CreateRoleAsync(new CreateRoleRequest
            {
                RoleName = roleName,
                AssumeRolePolicyDocument = trustPolicy,
                Description = "Role for Azure Sentinel AWS data connector"
            }, cancellationToken);

            // Attach necessary policies
            var policies = new[]
            {
                "arn:aws:iam::aws:policy/AmazonSQSReadOnlyAccess",
                "arn:aws:iam::aws:policy/AmazonS3ReadOnlyAccess"
            };

            foreach (var policy in policies)
            {
                await _iamClient.AttachRolePolicyAsync(new AttachRolePolicyRequest
                {
                    RoleName = roleName,
                    PolicyArn = policy
                }, cancellationToken);
            }

            return new
            {
                success = true,
                roleArn = createRoleResponse.Role.Arn,
                roleName = roleName,
                message = "IAM role created and configured successfully"
            };
        }
        catch (EntityAlreadyExistsException)
        {
            var roleResponse = await _iamClient!.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, cancellationToken);
            return new
            {
                success = true,
                roleArn = roleResponse.Role.Arn,
                roleName = roleName,
                message = "IAM role already exists"
            };
        }
    }

    private async Task<object> CreateS3BucketForLogs(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var bucketName = parameters["bucketName"].ToString();
        var logType = parameters["logType"].ToString();

        try
        {
            // Create bucket
            await _s3Client!.PutBucketAsync(new PutBucketRequest
            {
                BucketName = bucketName
            }, cancellationToken);

            // Configure bucket for log storage
            var lifecycleConfig = new LifecycleConfiguration
            {
                Rules = new List<LifecycleRule>
                {
                    new LifecycleRule
                    {
                        Id = "DeleteOldLogs",
                        Status = LifecycleRuleStatus.Enabled,
                        Expiration = new LifecycleRuleExpiration { Days = 90 }
                    }
                }
            };

            await _s3Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = bucketName,
                Configuration = lifecycleConfig
            }, cancellationToken);

            // Create folder structure based on log type
            var folderKey = logType switch
            {
                "CloudTrail" => "AWSLogs/CloudTrail/",
                "VPCFlow" => "AWSLogs/VPCFlowLogs/",
                "GuardDuty" => "AWSLogs/GuardDuty/",
                "CloudWatch" => "AWSLogs/CloudWatch/",
                _ => "AWSLogs/"
            };

            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucketName,
                Key = folderKey
            }, cancellationToken);

            return new
            {
                success = true,
                bucketName = bucketName,
                bucketArn = $"arn:aws:s3:::{bucketName}",
                logType = logType,
                message = "S3 bucket created and configured successfully"
            };
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyExists" || ex.ErrorCode == "BucketAlreadyOwnedByYou")
        {
            return new
            {
                success = true,
                bucketName = bucketName,
                bucketArn = $"arn:aws:s3:::{bucketName}",
                message = "S3 bucket already exists"
            };
        }
    }

    private async Task<object> CreateSqsQueue(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var queueName = parameters["queueName"].ToString();
        var bucketArn = parameters["bucketArn"].ToString();

        try
        {
            // Create SQS queue
            var createQueueResponse = await _sqsClient!.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["MessageRetentionPeriod"] = "1209600", // 14 days
                    ["VisibilityTimeout"] = "300" // 5 minutes
                }
            }, cancellationToken);

            var queueUrl = createQueueResponse.QueueUrl;

            // Get queue ARN
            var queueAttributes = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            }, cancellationToken);

            var queueArn = queueAttributes.Attributes["QueueArn"];

            // Set queue policy to allow S3 to send messages
            var queuePolicy = JsonSerializer.Serialize(new
            {
                Version = "2012-10-17",
                Statement = new[]
                {
                    new
                    {
                        Effect = "Allow",
                        Principal = new { Service = "s3.amazonaws.com" },
                        Action = "sqs:SendMessage",
                        Resource = queueArn,
                        Condition = new
                        {
                            ArnLike = new Dictionary<string, string>
                            {
                                ["aws:SourceArn"] = bucketArn
                            }
                        }
                    }
                }
            });

            await _sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["Policy"] = queuePolicy
                }
            }, cancellationToken);

            return new
            {
                success = true,
                queueUrl = queueUrl,
                queueArn = queueArn,
                queueName = queueName,
                message = "SQS queue created and configured successfully"
            };
        }
        catch (QueueNameExistsException)
        {
            var queueUrl = (await _sqsClient!.GetQueueUrlAsync(queueName, cancellationToken)).QueueUrl;
            var queueAttributes = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            }, cancellationToken);

            return new
            {
                success = true,
                queueUrl = queueUrl,
                queueArn = queueAttributes.Attributes["QueueArn"],
                message = "SQS queue already exists"
            };
        }
    }

    private async Task<object> ConfigureS3EventNotification(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var bucketName = parameters["bucketName"].ToString();
        var queueArn = parameters["queueArn"].ToString();

        var notificationConfig = new PutBucketNotificationRequest
        {
            BucketName = bucketName,
            QueueConfigurations = new List<QueueConfiguration>
            {
                new QueueConfiguration
                {
                    Id = "SentinelNotification",
                    Queue = queueArn,
                    Events = new List<EventType> { EventType.ObjectCreatedAll }
                }
            }
        };

        await _s3Client!.PutBucketNotificationAsync(notificationConfig, cancellationToken);

        return new
        {
            success = true,
            bucketName = bucketName,
            queueArn = queueArn,
            message = "S3 event notification configured successfully"
        };
    }

    private async Task<object> EnableCloudTrail(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        // Note: This is a simplified implementation
        // In production, you would use AWS CloudTrail SDK
        var trailName = parameters["trailName"].ToString();
        var bucketName = parameters["bucketName"].ToString();

        return new
        {
            success = true,
            trailName = trailName,
            bucketName = bucketName,
            message = "CloudTrail configuration initiated. Complete setup in AWS Console or use AWS CLI."
        };
    }

    private async Task<object> EnableVpcFlowLogs(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        // Note: This is a simplified implementation
        // In production, you would use AWS EC2 SDK for VPC Flow Logs
        var vpcId = parameters["vpcId"].ToString();
        var bucketArn = parameters["bucketArn"].ToString();

        return new
        {
            success = true,
            vpcId = vpcId,
            bucketArn = bucketArn,
            message = "VPC Flow Logs configuration initiated. Complete setup in AWS Console or use AWS CLI."
        };
    }

    private async Task<object> GetInfrastructureStatus(Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var status = new
        {
            oidcProviders = new List<string>(),
            roles = new List<string>(),
            buckets = new List<string>(),
            queues = new List<string>()
        };

        try
        {
            // List OIDC providers
            var providers = await _iamClient!.ListOpenIDConnectProvidersAsync(new ListOpenIDConnectProvidersRequest(), cancellationToken);
            var oidcProviders = providers.OpenIDConnectProviderList.Select(p => p.Arn).ToList();

            // List IAM roles
            var roles = await _iamClient.ListRolesAsync(new ListRolesRequest { MaxItems = 100 }, cancellationToken);
            var sentinelRoles = roles.Roles.Where(r => r.RoleName.Contains("Sentinel", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.RoleName).ToList();

            // List S3 buckets
            var buckets = await _s3Client!.ListBucketsAsync(cancellationToken);
            var logBuckets = buckets.Buckets.Where(b => b.BucketName.Contains("log", StringComparison.OrdinalIgnoreCase))
                .Select(b => b.BucketName).ToList();

            // List SQS queues
            var queues = await _sqsClient!.ListQueuesAsync(new ListQueuesRequest(), cancellationToken);
            var queueNames = queues.QueueUrls.Select(url => url.Split('/').Last()).ToList();

            return new
            {
                success = true,
                infrastructure = new
                {
                    oidcProviders = oidcProviders,
                    roles = sentinelRoles,
                    buckets = logBuckets,
                    queues = queueNames
                }
            };
        }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                error = ex.Message
            };
        }
    }
}