# Azure Key Vault Configuration Guide

This document explains how to configure Azure Key Vault for the ChatAgent application to securely store and retrieve the OpenAI API key.

## Overview

The ChatAgent application now supports Azure Key Vault integration for secure secret management. Instead of storing sensitive API keys in configuration files or environment variables, they are securely stored in Azure Key Vault and retrieved at runtime.

## Prerequisites

1. **Azure Subscription**: You need an active Azure subscription
2. **Azure Key Vault**: Create a Key Vault instance in Azure Portal
3. **OpenAI API Key**: Your OpenAI API key to store in the vault

## Azure Key Vault Setup

### Step 1: Create Azure Key Vault

```bash
# Using Azure CLI
az keyvault create \
  --name "your-keyvault-name" \
  --resource-group "your-resource-group" \
  --location "eastus"
```

### Step 2: Store OpenAI API Key

```bash
# Add the OpenAI API key as a secret
az keyvault secret set \
  --vault-name "your-keyvault-name" \
  --name "OpenAIApiKey" \
  --value "your-openai-api-key"
```

### Step 3: Configure Access

You can use either **Managed Identity** (recommended for production) or **Service Principal** (for local development).

## Configuration Options

### Option 1: Managed Identity (Production - Recommended)

Best for applications deployed to Azure (App Service, Container Instances, VMs, etc.)

1. **Enable Managed Identity on your Azure resource**:
   ```bash
   # For App Service
   az webapp identity assign \
     --name "your-app-name" \
     --resource-group "your-resource-group"
   ```

2. **Grant Key Vault access to the Managed Identity**:
   ```bash
   # Get the principal ID from the previous command output
   az keyvault set-policy \
     --name "your-keyvault-name" \
     --object-id "principal-id-from-previous-step" \
     --secret-permissions get list
   ```

3. **Update appsettings.json**:
   ```json
   {
     "AzureKeyVault": {
       "VaultUri": "https://your-keyvault-name.vault.azure.net/",
       "UseManagedIdentity": true
     },
     "OpenAI": {
       "ApiKeySecretName": "OpenAIApiKey",
       "Model": "gpt-4"
     }
   }
   ```

### Option 2: Service Principal (Local Development)

Best for local development and testing.

1. **Create a Service Principal**:
   ```bash
   az ad sp create-for-rbac \
     --name "ChatAgentKeyVaultAccess" \
     --role contributor \
     --scopes /subscriptions/{subscription-id}/resourceGroups/{resource-group}
   ```

2. **Grant Key Vault access**:
   ```bash
   az keyvault set-policy \
     --name "your-keyvault-name" \
     --spn "service-principal-client-id" \
     --secret-permissions get list
   ```

3. **Update appsettings.json or appsettings.Development.json**:
   ```json
   {
     "AzureKeyVault": {
       "VaultUri": "https://your-keyvault-name.vault.azure.net/",
       "TenantId": "your-tenant-id",
       "ClientId": "service-principal-client-id",
       "ClientSecret": "service-principal-client-secret",
       "UseManagedIdentity": false
     },
     "OpenAI": {
       "ApiKeySecretName": "OpenAIApiKey",
       "Model": "gpt-4"
     }
   }
   ```

### Option 3: Azure CLI Authentication (Local Development Alternative)

For developers who have Azure CLI installed and authenticated.

1. **Login to Azure CLI**:
   ```bash
   az login
   ```

2. **Grant your user account access to Key Vault**:
   ```bash
   az keyvault set-policy \
     --name "your-keyvault-name" \
     --upn "your-email@domain.com" \
     --secret-permissions get list
   ```

3. **Update appsettings.json**:
   ```json
   {
     "AzureKeyVault": {
       "VaultUri": "https://your-keyvault-name.vault.azure.net/",
       "UseManagedIdentity": false
     },
     "OpenAI": {
       "ApiKeySecretName": "OpenAIApiKey",
       "Model": "gpt-4"
     }
   }
   ```

## Fallback Options

The application includes fallback mechanisms for flexibility:

1. **Key Vault** (Primary) → Attempts to load from Azure Key Vault
2. **Environment Variable** (Fallback) → Checks `OPENAI_API_KEY` environment variable
3. **Error** → Throws exception if no API key is found

## How It Works

1. **Program.cs** calls `ConfigureKeyVault()` at startup
2. The method checks for Azure Key Vault configuration
3. If configured, it adds Key Vault as a configuration provider
4. Secrets from Key Vault are mapped to configuration keys
5. The OpenAI API key is retrieved using: `Configuration["OpenAI:ApiKey"]` or `Configuration["OpenAIApiKey"]`

## Security Benefits

- **No secrets in code**: API keys are never stored in source code
- **Centralized management**: All secrets in one secure location
- **Access control**: Fine-grained permissions using Azure RBAC
- **Audit logging**: Track who accessed secrets and when
- **Rotation support**: Easy to rotate keys without code changes
- **Environment isolation**: Different vaults for dev/staging/production

## Troubleshooting

### Common Issues

1. **"Access denied" error**:
   - Verify the identity has proper permissions on Key Vault
   - Check the secret permissions include "Get" and "List"

2. **"Key Vault not found"**:
   - Verify the VaultUri is correct
   - Ensure the Key Vault exists in your subscription

3. **"Secret not found"**:
   - Verify the secret name matches exactly (case-sensitive)
   - Check the secret exists in the Key Vault

### Debug Tips

- Check console output for Key Vault configuration messages
- The root endpoint (`/`) shows Key Vault configuration status
- Use Azure Portal to verify Key Vault access policies
- Check Application Insights for detailed error logs

## Local Development Without Key Vault

For quick local testing without Azure Key Vault:

```bash
# Set environment variable
export OPENAI_API_KEY="your-api-key"

# Or in Windows
set OPENAI_API_KEY=your-api-key

# Run the application
dotnet run
```

## Production Deployment Checklist

- [ ] Create production Key Vault
- [ ] Store OpenAI API key in Key Vault
- [ ] Enable Managed Identity on Azure resource
- [ ] Grant Key Vault access to Managed Identity
- [ ] Update production configuration
- [ ] Remove any hardcoded secrets
- [ ] Test secret retrieval
- [ ] Enable Key Vault diagnostic logging
- [ ] Set up alerts for access failures

## Additional Resources

- [Azure Key Vault Documentation](https://docs.microsoft.com/azure/key-vault/)
- [Managed Identities for Azure Resources](https://docs.microsoft.com/azure/active-directory/managed-identities-azure-resources/)
- [Azure Key Vault Configuration Provider](https://docs.microsoft.com/aspnet/core/security/key-vault-configuration)