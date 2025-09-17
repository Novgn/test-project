using Microsoft.SemanticKernel;
using ChatAgent.Application.Orchestration;
using ChatAgent.Application.Plugins;
using ChatAgent.Domain.Interfaces;
using ChatAgent.Infrastructure.McpTools;
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
                .AllowCredentials();
        });
});

// ===== Configure Semantic Kernel =====
var kernelBuilder = Kernel.CreateBuilder();

// Check if we're using Azure OpenAI or OpenAI directly
// var useAzureOpenAI = builder.Configuration.GetValue<bool>("AzureOpenAI:Enabled", false);

// if (useAzureOpenAI)
// {
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
    endpoint: endpoint,
    apiKey: apiKey);
// }
// else
// {
//     // Standard OpenAI configuration
//     var apiKeySecretName = builder.Configuration["OpenAI:ApiKeySecretName"] ?? "OpenAIApiKey";
//     var openAiApiKey = builder.Configuration[apiKeySecretName]; // Direct secret name

//     // If not found by secret name, try nested configuration key
//     if (string.IsNullOrEmpty(openAiApiKey))
//     {
//         openAiApiKey = builder.Configuration["OpenAI:ApiKey"];
//     }

//     // If not in Key Vault, fallback to environment variable for local development
//     if (string.IsNullOrEmpty(openAiApiKey))
//     {
//         openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
//     }

//     // Validate API key is available
//     if (string.IsNullOrEmpty(openAiApiKey))
//     {
//         throw new InvalidOperationException(
//             "OpenAI API Key not configured. Please set it in Azure Key Vault or as an environment variable.");
//     }

//     kernelBuilder.Services.AddOpenAIChatCompletion(
//         modelId: builder.Configuration["OpenAI:Model"] ?? "gpt-4",
//         apiKey: openAiApiKey);
// }

kernelBuilder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.AddConsole();
    loggingBuilder.SetMinimumLevel(LogLevel.Debug);
});

var kernel = kernelBuilder.Build();
builder.Services.AddSingleton(kernel);

// Register repositories
builder.Services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();

// ===== Configure MCP Tool Providers =====
// Example: File system MCP server using npx
builder.Services.AddSingleton<IMcpToolProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<McpServerToolProvider>>();

    return new McpServerToolProvider(
        "filesystem",
        "File system operations MCP server",
        "npx",
        new[] { "-y", "@modelcontextprotocol/server-filesystem", "/tmp" },
        logger);
});

// Example: Everything MCP server (includes multiple tools)
builder.Services.AddSingleton<IMcpToolProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<McpServerToolProvider>>();

    return new McpServerToolProvider(
        "everything",
        "MCP server with multiple tools",
        "npx",
        new[] { "-y", "@modelcontextprotocol/server-everything" },
        logger);
});

// Register the MCP tool plugin
builder.Services.AddSingleton<McpToolPlugin>();

// Register the orchestrator with MCP support
builder.Services.AddSingleton<IOrchestrator>(provider =>
{
    var kernel = provider.GetRequiredService<Kernel>();
    var conversationRepo = provider.GetRequiredService<IConversationRepository>();
    var logger = provider.GetRequiredService<ILogger<SemanticKernelOrchestrator>>();
    var mcpProviders = provider.GetServices<IMcpToolProvider>();
    var mcpPluginLogger = provider.GetRequiredService<ILogger<McpToolPlugin>>();

    return new SemanticKernelOrchestrator(
        kernel,
        conversationRepo,
        logger,
        mcpProviders,
        mcpPluginLogger);
});

var app = builder.Build();

// Configure the HTTP request pipeline
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
app.UseCors("AllowReactApp");
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