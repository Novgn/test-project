using Microsoft.SemanticKernel;
using ChatAgent.Application.Orchestration;
using ChatAgent.Application.Plugins;
using ChatAgent.Application.Plugins.Azure;
using ChatAgent.Application.Plugins.Coordinator;
using ChatAgent.Application.Tools.Azure;
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

builder.Services.AddSignalR();

// Configure CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy =>
        {
            policy.WithOrigins(
                    "http://localhost:3000",
                    "http://localhost:5173",
                    "https://localhost:3000",
                    "https://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .WithExposedHeaders("*");
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

// Register the Sentinel Connector Group Chat Orchestrator
builder.Services.AddSingleton(provider =>
{
    var kernel = provider.GetRequiredService<Kernel>();
    var logger = provider.GetRequiredService<ILogger<SentinelConnectorGroupChatOrchestrator>>();
    var conversationRepo = provider.GetRequiredService<IConversationRepository>();

    return new SentinelConnectorGroupChatOrchestrator(
        kernel,
        logger,
        conversationRepo,
        provider);
});

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
builder.Services.AddSingleton(provider =>
    new AzureToolHandlers(provider.GetRequiredService<FindConnectorSolutionHandler>()));
builder.Services.AddSingleton<AzurePlugin>();

// Register plugin loggers
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<ILoggerFactory>().CreateLogger<CoordinatorPlugin>());
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<ILoggerFactory>().CreateLogger<AzurePlugin>());

// Register the orchestrator - use SentinelConnectorGroupChatOrchestrator as the primary
builder.Services.AddSingleton<IOrchestrator>(provider =>
{
    // Use the SentinelConnectorGroupChatOrchestrator which has the proper plugins configured
    return provider.GetRequiredService<SentinelConnectorGroupChatOrchestrator>();
});


var app = builder.Build();

// Configure the HTTP request pipeline

// CORS must be early in the pipeline
app.UseCors("AllowReactApp");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Only use HTTPS redirection in production
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
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