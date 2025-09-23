# AWS-Azure Sentinel Connector Setup Guide

## Overview
This application automates the setup of AWS-Azure Sentinel connector using a multi-agent AI orchestration system powered by Microsoft Semantic Kernel.

## Prerequisites

### AWS Requirements
- AWS Account with appropriate permissions
- IAM permissions to create:
  - OIDC identity providers
  - IAM roles and policies
  - S3 buckets
  - SQS queues
  - CloudTrail configurations

### Azure Requirements
- Azure subscription
- Azure Sentinel workspace deployed
- Service Principal or Managed Identity with permissions to:
  - Deploy ARM templates
  - Configure Sentinel data connectors
  - Access Log Analytics workspace

## Configuration

### 1. AWS Credentials

Edit `src/ChatAgent.WebAPI/appsettings.Development.json`:

```json
"AWS": {
  "Region": "us-east-1",
  "AccessKeyId": "YOUR_AWS_ACCESS_KEY",
  "SecretAccessKey": "YOUR_AWS_SECRET_KEY",
  "UseProfile": false
}
```

Or use AWS CLI profile:
```json
"AWS": {
  "Profile": "default",
  "UseProfile": true
}
```

### 2. Azure Credentials

Configure Azure service principal:
```json
"Azure": {
  "TenantId": "YOUR_TENANT_ID",
  "ClientId": "YOUR_CLIENT_ID",
  "ClientSecret": "YOUR_CLIENT_SECRET",
  "SubscriptionId": "YOUR_SUBSCRIPTION_ID",
  "UseManagedIdentity": false
}
```

### 3. Azure OpenAI Configuration

```json
"AzureOpenAI": {
  "Enabled": true,
  "Endpoint": "https://your-resource.openai.azure.com/",
  "DeploymentName": "gpt-4",
  "ApiKey": "YOUR_API_KEY"
}
```

## Running the Application

### Backend API
```bash
cd src/ChatAgent.WebAPI
dotnet run
```

### Frontend UI
```bash
cd src/ChatAgent.FrontEnd
npm install
npm run dev
```

## Using the Sentinel Connector

1. Navigate to http://localhost:5173/sentinel-connector

2. Fill in the configuration:
   - **Azure Subscription ID**: Your Azure subscription ID
   - **Azure Tenant ID**: Your Azure AD tenant ID
   - **Sentinel Workspace ID**: Your Log Analytics workspace ID
   - **Resource Group Name**: Resource group containing Sentinel
   - **AWS Region**: AWS region for resources (e.g., us-east-1)
   - **Log Types**: Select which AWS logs to ingest

3. Click "Start Setup"

## What Happens During Setup

The multi-agent system orchestrates the following:

### Phase 1: Validation
- CoordinatorAgent validates prerequisites
- Checks AWS and Azure credentials
- Verifies Sentinel workspace exists

### Phase 2: AWS Infrastructure
- AwsAgent creates OIDC identity provider
- Sets up IAM role with web identity trust
- Creates S3 buckets for log storage
- Configures SQS queues for notifications
- Enables CloudTrail/VPC Flow Logs/GuardDuty

### Phase 3: Azure Configuration
- AzureAgent deploys AWS connector solution
- Configures data connector in Sentinel
- Sets up ingestion rules

### Phase 4: Integration
- IntegrationAgent validates OIDC authentication
- Tests SQS queue connectivity
- Verifies log format compatibility

### Phase 5: Monitoring
- MonitorAgent checks data flow
- Validates log ingestion
- Confirms setup completion

## Real-Time Updates

The UI provides real-time updates via SignalR showing:
- Current phase progress
- Agent messages and actions
- Resource creation details
- Any errors or warnings

## Security Notes

1. **Never commit credentials** to source control
2. Use environment variables or Azure Key Vault for production
3. Follow principle of least privilege for IAM roles
4. Enable MFA on AWS and Azure accounts
5. Regularly rotate credentials

## Troubleshooting

### AWS Errors
- Ensure IAM user has required permissions
- Check AWS region availability
- Verify S3 bucket naming compliance

### Azure Errors
- Confirm service principal permissions
- Validate Sentinel workspace ID
- Check Azure subscription quotas

### Connection Issues
- Verify CORS configuration
- Check SignalR connection
- Ensure ports 5000 (API) and 5173 (UI) are available

## Environment Variables (Alternative)

Instead of appsettings.json, you can use environment variables:

```bash
# AWS
export AWS_ACCESS_KEY_ID=your_key
export AWS_SECRET_ACCESS_KEY=your_secret
export AWS_REGION=us-east-1

# Azure
export AZURE_TENANT_ID=your_tenant
export AZURE_CLIENT_ID=your_client
export AZURE_CLIENT_SECRET=your_secret
export AZURE_SUBSCRIPTION_ID=your_subscription

# OpenAI
export AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
export AZURE_OPENAI_KEY=your_key
```

## Production Deployment

For production:
1. Use Azure Managed Identity instead of service principal
2. Store secrets in Azure Key Vault
3. Enable HTTPS with valid certificates
4. Implement rate limiting and authentication
5. Set up monitoring and alerting
6. Use production-grade database for conversation storage

## Support

For issues or questions, check:
- Azure Sentinel documentation: https://docs.microsoft.com/azure/sentinel/
- AWS CloudTrail documentation: https://docs.aws.amazon.com/cloudtrail/
- Semantic Kernel: https://github.com/microsoft/semantic-kernel