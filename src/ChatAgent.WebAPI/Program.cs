using Microsoft.SemanticKernel;
using ChatAgent.Application.Orchestration;
using ChatAgent.Application.Plugins.Azure;
using ChatAgent.Application.Plugins.AWS;
using ChatAgent.Application.Plugins.Coordinator;
using ChatAgent.Application.Tools.Azure;
using ChatAgent.Application.Tools.AWS;
using ChatAgent.Application.Tools.Coordinator;
using ChatAgent.Domain.Interfaces;
using ChatAgent.Infrastructure.Repositories;
using ChatAgent.Infrastructure.SignalR;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// ===== Configure Azure Key Vault =====
// This section sets up Azure Key Vault to securely retrieve secrets like the OpenAI API key
ConfigureKeyVault(builder);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// Configure CORS for React frontend and SignalR
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy =>
        {
            policy.WithOrigins(
                    "http://localhost:3000",
                    "http://localhost:5173",
                    "https://localhost:3000",
                    "https://localhost:5173",
                    "http://127.0.0.1:5173",
                    "http://127.0.0.1:3000")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        });
});

// ===== Configure Semantic Kernel =====
var kernelBuilder = Kernel.CreateBuilder();

// Azure OpenAI configuration
var endpoint = builder.Configuration["AzureOpenAI:Endpoint"];
var deploymentName = builder.Configuration["AzureOpenAI:DeploymentName"];
var apiKey = builder.Configuration["AzureOpenAIApiKey"]; // From Key Vault

if (string.IsNullOrEmpty(apiKey))
{
    apiKey = builder.Configuration["AzureOpenAI:ApiKey"];
}

if (string.IsNullOrEmpty(apiKey))
{
    apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
}

if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(deploymentName) || string.IsNullOrEmpty(apiKey))
{
    throw new InvalidOperationException(
        "Azure OpenAI configuration incomplete. Please configure Endpoint, DeploymentName, and API Key.");
}

kernelBuilder.Services.AddAzureOpenAIChatCompletion(
    deploymentName: deploymentName,
    modelId: "gpt-4",
    endpoint: endpoint,
    apiKey: apiKey);

kernelBuilder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.AddConsole();
    loggingBuilder.SetMinimumLevel(LogLevel.Debug);
});

var kernel = kernelBuilder.Build();
builder.Services.AddSingleton(kernel);

// Register repositories
builder.Services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();

// Register the new working Orchestrator
builder.Services.AddSingleton(provider =>
{
    var kernel = provider.GetRequiredService<Kernel>();
    var logger = provider.GetRequiredService<ILogger<Orchestrator>>();
    var conversationRepo = provider.GetRequiredService<IConversationRepository>();

    return new Orchestrator(
        kernel,
        logger,
        conversationRepo,
        provider);
});

// Removed archived SentinelConnectorGroupChatOrchestrator

// Register Coordinator plugin and handlers
builder.Services.AddSingleton<ValidatePrerequisitesHandler>();
builder.Services.AddSingleton<PlanConnectorSetupHandler>();
builder.Services.AddSingleton<GenerateSetupReportHandler>();
builder.Services.AddSingleton(provider =>
    new CoordinatorToolHandlers(
        provider.GetRequiredService<ValidatePrerequisitesHandler>(),
        provider.GetRequiredService<PlanConnectorSetupHandler>(),
        provider.GetRequiredService<GenerateSetupReportHandler>()));
builder.Services.AddSingleton<CoordinatorPlugin>();

// Register Azure plugin and handlers
builder.Services.AddSingleton<FindConnectorSolutionHandler>();
builder.Services.AddSingleton<InstallConnectorSolutionHandler>();
builder.Services.AddSingleton(provider =>
    new AzureToolHandlers(
        provider.GetRequiredService<FindConnectorSolutionHandler>(),
        provider.GetRequiredService<InstallConnectorSolutionHandler>()));
builder.Services.AddSingleton<AzurePlugin>();

// Register AWS SDK clients
builder.Services.AddSingleton<Amazon.IdentityManagement.IAmazonIdentityManagementService>(provider =>
{
    var config = new Amazon.IdentityManagement.AmazonIdentityManagementServiceConfig();
    // Configure region if needed from configuration
    var region = builder.Configuration["AWS:Region"];
    if (!string.IsNullOrEmpty(region))
    {
        config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
    }
    return new Amazon.IdentityManagement.AmazonIdentityManagementServiceClient(config);
});

builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(provider =>
{
    var config = new Amazon.S3.AmazonS3Config();
    // Configure region if needed from configuration
    var region = builder.Configuration["AWS:Region"];
    if (!string.IsNullOrEmpty(region))
    {
        config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
    }
    return new Amazon.S3.AmazonS3Client(config);
});

builder.Services.AddSingleton<Amazon.SQS.IAmazonSQS>(provider =>
{
    var config = new Amazon.SQS.AmazonSQSConfig();
    // Configure region if needed from configuration
    var region = builder.Configuration["AWS:Region"];
    if (!string.IsNullOrEmpty(region))
    {
        config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
    }
    return new Amazon.SQS.AmazonSQSClient(config);
});

builder.Services.AddSingleton<Amazon.CloudTrail.IAmazonCloudTrail>(provider =>
{
    var config = new Amazon.CloudTrail.AmazonCloudTrailConfig();
    // Configure region if needed from configuration
    var region = builder.Configuration["AWS:Region"];
    if (!string.IsNullOrEmpty(region))
    {
        config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
    }
    return new Amazon.CloudTrail.AmazonCloudTrailClient(config);
});

// Register AWS plugin and handlers
builder.Services.AddSingleton<SetupAWSAuthHandler>();
builder.Services.AddSingleton<SetupAWSInfraHandler>();
builder.Services.AddSingleton(provider =>
    new AWSToolHandlers(
        provider.GetRequiredService<SetupAWSAuthHandler>(),
        provider.GetRequiredService<SetupAWSInfraHandler>()));
builder.Services.AddSingleton<AWSPlugin>();

// Register plugin loggers
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<ILoggerFactory>().CreateLogger<CoordinatorPlugin>());
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<ILoggerFactory>().CreateLogger<AzurePlugin>());

// Removed archived SimpleGroupChatOrchestrator

// Register the orchestrator - using the new working Orchestrator
builder.Services.AddSingleton<IOrchestrator>(provider =>
{
    return provider.GetRequiredService<Orchestrator>();
});


var app = builder.Build();

// Configure the HTTP request pipeline

// Only use HTTPS redirection in production
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Swagger must come before routing
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Routing must come before CORS
app.UseRouting();

// CORS must come after UseRouting and before UseEndpoints
app.UseCors("AllowReactApp");

app.UseAuthorization();

// Map endpoints after all middleware
app.MapControllers();
app.MapHub<ChatHub>("/chathub");

// Root endpoint showing service information
app.MapGet("/", () => new
{
    service = "ChatAgent WebAPI",
    version = "1.0.0",
    signalRHub = "/chathub",
    swagger = "/swagger",
    keyVault = new
    {
        configured = !string.IsNullOrEmpty(builder.Configuration["AzureKeyVault:VaultUri"]),
        mode = builder.Configuration["AzureKeyVault:UseManagedIdentity"] == "true"
            ? "Managed Identity"
            : "Service Principal"
    },
    mcp = new
    {
        enabled = true,
        providers = GetMcpProviderDescriptions()
    }
});

app.Run();

// Static array for MCP provider descriptions to avoid repeated allocations
static string[] GetMcpProviderDescriptions() =>
[
    "filesystem - File system operations",
    "everything - Multiple tool examples"
];

/// <summary>
/// Configures Azure Key Vault for secret management
/// Supports both Managed Identity (for Azure deployment) and Service Principal (for local dev)
/// </summary>
static void ConfigureKeyVault(WebApplicationBuilder builder)
{
    var keyVaultUri = builder.Configuration["AzureKeyVault:VaultUri"];

    // Only configure Key Vault if URI is provided
    if (string.IsNullOrEmpty(keyVaultUri))
    {
        Console.WriteLine("Azure Key Vault URI not configured. Skipping Key Vault configuration.");
        return;
    }

    try
    {
        var useManagedIdentity = builder.Configuration.GetValue<bool>("AzureKeyVault:UseManagedIdentity");

        if (useManagedIdentity)
        {
            // ===== Managed Identity Configuration =====
            // Use this for production deployments in Azure
            // The application will authenticate using its managed identity
            Console.WriteLine("Configuring Azure Key Vault with Managed Identity...");

            // Try system-assigned managed identity first, then user-assigned
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeEnvironmentCredential = false,
                ExcludeInteractiveBrowserCredential = true,
                ExcludeAzureCliCredential = false,
                ExcludeAzurePowerShellCredential = false,
                // SharedTokenCacheCredential is deprecated - excluding it is no longer needed
                ExcludeVisualStudioCredential = false,
                ExcludeVisualStudioCodeCredential = false,
                ExcludeManagedIdentityCredential = false,
            });

            builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
        }
        else
        {
            // ===== Service Principal Configuration =====
            // Use this for local development or when managed identity is not available
            var tenantId = builder.Configuration["AzureKeyVault:TenantId"];
            var clientId = builder.Configuration["AzureKeyVault:ClientId"];
            var clientSecret = builder.Configuration["AzureKeyVault:ClientSecret"];

            if (!string.IsNullOrEmpty(tenantId) &&
                !string.IsNullOrEmpty(clientId) &&
                !string.IsNullOrEmpty(clientSecret))
            {
                Console.WriteLine("Configuring Azure Key Vault with Service Principal...");

                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
            }
            else
            {
                Console.WriteLine("Service Principal credentials not fully configured. Attempting default credentials...");

                // Fallback to DefaultAzureCredential which tries multiple auth methods
                var credential = new DefaultAzureCredential();
                builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
            }
        }

        // Load specific secret names if configured
        var apiKeySecretName = builder.Configuration["AzureOpenAI:ApiKeySecretName"];
        if (!string.IsNullOrEmpty(apiKeySecretName))
        {
            Console.WriteLine($"OpenAI API key will be loaded from Key Vault secret: {apiKeySecretName}");

            // After Key Vault is configured, the secret value will be available at:
            // Configuration["OpenAI:ApiKey"] or Configuration[apiKeySecretName]
            // The Azure Key Vault configuration provider maps secrets to configuration keys
        }

        Console.WriteLine("Azure Key Vault configuration completed successfully.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Failed to configure Azure Key Vault: {ex.Message}");
        Console.WriteLine("Falling back to local configuration or environment variables.");
    }
}