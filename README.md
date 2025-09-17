# Chat Agent - Multi-Agent System with MCP Tools

A C# multi-agent chat system built with Clean Architecture, Semantic Kernel orchestration, MCP server tools integration, and SignalR for real-time React frontend communication.

## Architecture Overview

### Clean Architecture Layers

1. **Domain Layer** (`ChatAgent.Domain`)
   - Core entities: `ChatMessage`, `Agent`, `Conversation`
   - Interfaces: `IOrchestrator`, `IMcpToolProvider`, `IConversationRepository`
   - No external dependencies

2. **Application Layer** (`ChatAgent.Application`)
   - Semantic Kernel orchestration (`SemanticKernelOrchestrator`)
   - Sequential agent processing
   - MCP tool plugins
   - Business logic implementation

3. **Infrastructure Layer** (`ChatAgent.Infrastructure`)
   - MCP server integration (`McpServerToolProvider`)
   - SignalR hub implementation (`ChatHub`)
   - Repository implementations (`InMemoryConversationRepository`)

4. **WebAPI Layer** (`ChatAgent.WebAPI`)
   - ASP.NET Core Web API
   - SignalR endpoints
   - Dependency injection configuration
   - CORS setup for React frontend

## Key Features

### Multi-Agent Orchestration
- **Coordinator Agent**: Manages flow between specialized agents
- **Analysis Agent**: Analyzes user input and determines intent
- **Execution Agent**: Executes tasks using MCP tools

### Sequential Processing Flow
1. Message analysis (intent detection)
2. Routing decision based on capabilities
3. Agent execution
4. Response generation

### MCP Tool Integration
- Pluggable MCP server providers
- Dynamic tool discovery
- Parameter validation
- Async execution with cancellation support

### Real-time Communication
- SignalR hub for bi-directional communication
- Session management
- Conversation history
- Agent discovery endpoints

## Setup Instructions

### Prerequisites
- .NET 8.0 SDK
- Node.js (for React frontend)
- OpenAI API key

### Backend Setup

1. **Configure OpenAI API Key**
   ```bash
   export OPENAI_API_KEY="your-api-key"
   ```
   Or add to `appsettings.json`:
   ```json
   {
     "OpenAI": {
       "ApiKey": "your-api-key",
       "Model": "gpt-4"
     }
   }
   ```

2. **Build and Run**
   ```bash
   dotnet build
   cd src/ChatAgent.WebAPI
   dotnet run
   ```

   The API will be available at:
   - HTTP: http://localhost:5000
   - HTTPS: https://localhost:5001
   - Swagger: https://localhost:5001/swagger
   - SignalR Hub: https://localhost:5001/chathub

### Frontend Setup

1. **Install SignalR client**
   ```bash
   npm install @microsoft/signalr
   ```

2. **Use the provided React component**
   - Copy `react-frontend-sample.tsx` to your React project
   - Import and use the `ChatAgent` component

### MCP Server Configuration

Configure MCP servers in `appsettings.json`:
```json
{
  "MCP": {
    "Servers": {
      "FileSystem": {
        "Url": "http://localhost:3001",
        "Description": "File system operations"
      },
      "WebSearch": {
        "Url": "http://localhost:3002",
        "Description": "Web search capabilities"
      }
    }
  }
}
```

## SignalR Hub Methods

### Client-to-Server
- `SendMessage(string message)`: Send a message for processing
- `GetConversationHistory()`: Retrieve conversation history
- `GetAvailableAgents()`: List available agents

### Server-to-Client Events
- `Connected`: Connection established with session info
- `ReceiveMessage`: Receive agent response
- `Processing`: Message being processed
- `ConversationHistory`: Conversation history data
- `AvailableAgents`: List of available agents
- `Error`: Error messages

## Extending the System

### Adding New Agents

1. Create agent in `SemanticKernelOrchestrator.InitializeDefaultAgents()`:
```csharp
var customAgent = new Agent("custom", "Custom Agent", "Description", AgentType.Specialist);
customAgent.AddCapability("custom-capability");
_agents[customAgent.Id] = customAgent;
```

2. Update routing logic in `DetermineRoutingAsync()`

### Adding MCP Providers

1. Implement `IMcpToolProvider` or use `McpServerToolProvider`
2. Register in `Program.cs`:
```csharp
builder.Services.AddSingleton<IMcpToolProvider>(provider =>
    new McpServerToolProvider("name", "description", "url", logger));
```

### Custom Orchestration Strategies

Implement `IOrchestrator` interface for custom orchestration logic.

## Running the Application

1. Start the backend:
   ```bash
   cd src/ChatAgent.WebAPI
   dotnet run
   ```

2. Start your React application with the provided component

3. Connect and start chatting!

## Dependencies

### NuGet Packages
- Microsoft.SemanticKernel (1.32.0)
- Microsoft.SemanticKernel.Connectors.OpenAI (1.32.0)
- Microsoft.AspNetCore.SignalR.Core (8.0.0)
- McpDotNet (0.5.0)
- Swashbuckle.AspNetCore (6.5.0)

### Frontend
- @microsoft/signalr
- React
- TypeScript

## Notes

- The system uses in-memory storage by default
- MCP servers need to be running separately
- CORS is configured for local development (ports 3000, 5173)