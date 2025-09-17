#!/usr/bin/env node

/**
 * End-to-End Test for Conversational AI with Multi-Agent Orchestration
 *
 * This script tests the complete flow:
 * 1. Connects to the SignalR hub
 * 2. Sends messages to test different agent specializations
 * 3. Validates responses from the orchestrated agents
 */

const signalR = require("@microsoft/signalr");

const API_URL = "http://localhost:5000";
const HUB_URL = `${API_URL}/chathub`;

// Test messages to trigger different agent paths
const TEST_MESSAGES = [
    {
        message: "Can you list the files in the current directory?",
        expectedAgentPath: "file",
        description: "Testing file agent with file system operations"
    },
    {
        message: "What's the latest news about artificial intelligence?",
        expectedAgentPath: "web",
        description: "Testing web agent with search capabilities"
    },
    {
        message: "Analyze this data: [1, 2, 3, 4, 5]. What's the average?",
        expectedAgentPath: "data",
        description: "Testing data agent with analysis capabilities"
    },
    {
        message: "Hello, how can you help me today?",
        expectedAgentPath: "general",
        description: "Testing general conversation flow"
    }
];

class ConversationalAITester {
    constructor() {
        this.connection = null;
        this.sessionId = null;
        this.testResults = [];
    }

    async connect() {
        console.log("🔌 Connecting to SignalR hub...");

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(HUB_URL)
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Information)
            .build();

        // Set up event handlers
        this.setupEventHandlers();

        try {
            await this.connection.start();
            console.log("✅ Connected to SignalR hub");
            return true;
        } catch (err) {
            console.error("❌ Failed to connect:", err);
            return false;
        }
    }

    setupEventHandlers() {
        // Handle connection established
        this.connection.on("Connected", (data) => {
            this.sessionId = data.sessionId;
            console.log(`📋 Session ID: ${this.sessionId}`);
            console.log(`🔗 Connection ID: ${data.connectionId}`);
        });

        // Handle processing status
        this.connection.on("Processing", (data) => {
            console.log("⚙️  Processing message...");
        });

        // Handle received messages
        this.connection.on("ReceiveMessage", (data) => {
            console.log("\n📨 Received Response:");
            console.log(`   Agent: ${data.agentId || 'Unknown'}`);
            console.log(`   Role: ${data.role}`);
            console.log(`   Message: ${data.message.substring(0, 200)}${data.message.length > 200 ? '...' : ''}`);

            if (data.metadata) {
                console.log(`   Metadata:`, data.metadata);
            }

            this.testResults.push({
                request: this.currentTest,
                response: data,
                success: true
            });
        });

        // Handle errors
        this.connection.on("Error", (error) => {
            console.error("❌ Error:", error);
            this.testResults.push({
                request: this.currentTest,
                error: error,
                success: false
            });
        });

        // Handle conversation history
        this.connection.on("ConversationHistory", (data) => {
            console.log("\n📚 Conversation History:");
            console.log(`   Session: ${data.sessionId}`);
            console.log(`   Status: ${data.status}`);
            console.log(`   Messages: ${data.messages.length}`);
        });

        // Handle available agents
        this.connection.on("AvailableAgents", (agents) => {
            console.log("\n🤖 Available Agents:");
            agents.forEach(agent => {
                console.log(`   - ${agent.name} (${agent.id})`);
                console.log(`     Type: ${agent.type}`);
                console.log(`     Description: ${agent.description}`);
                if (agent.capabilities && agent.capabilities.length > 0) {
                    console.log(`     Capabilities: ${agent.capabilities.join(', ')}`);
                }
            });
        });
    }

    async sendMessage(message) {
        if (!this.connection) {
            console.error("Not connected to hub");
            return;
        }

        try {
            await this.connection.invoke("SendMessage", message);
            // Wait for response
            await this.waitForResponse();
        } catch (err) {
            console.error("Failed to send message:", err);
        }
    }

    async waitForResponse(timeout = 10000) {
        return new Promise((resolve) => {
            setTimeout(resolve, timeout);
        });
    }

    async getAvailableAgents() {
        if (!this.connection) {
            console.error("Not connected to hub");
            return;
        }

        try {
            await this.connection.invoke("GetAvailableAgents");
            await this.waitForResponse(2000);
        } catch (err) {
            console.error("Failed to get agents:", err);
        }
    }

    async getConversationHistory() {
        if (!this.connection) {
            console.error("Not connected to hub");
            return;
        }

        try {
            await this.connection.invoke("GetConversationHistory");
            await this.waitForResponse(2000);
        } catch (err) {
            console.error("Failed to get history:", err);
        }
    }

    async runTests() {
        console.log("\n🧪 Starting Conversational AI Tests\n");
        console.log("=" .repeat(50));

        // First, get available agents
        console.log("\n1️⃣  Fetching available agents...");
        await this.getAvailableAgents();

        // Run each test message
        for (let i = 0; i < TEST_MESSAGES.length; i++) {
            const test = TEST_MESSAGES[i];
            this.currentTest = test;

            console.log("\n" + "=" .repeat(50));
            console.log(`\n${i + 2}️⃣  Test: ${test.description}`);
            console.log(`   Message: "${test.message}"`);
            console.log(`   Expected Path: ${test.expectedAgentPath}`);

            await this.sendMessage(test.message);
        }

        // Get final conversation history
        console.log("\n" + "=" .repeat(50));
        console.log("\n📊 Fetching conversation history...");
        await this.getConversationHistory();

        // Print test summary
        this.printSummary();
    }

    printSummary() {
        console.log("\n" + "=" .repeat(50));
        console.log("\n📈 Test Summary\n");

        const successful = this.testResults.filter(r => r.success).length;
        const failed = this.testResults.filter(r => !r.success).length;

        console.log(`✅ Successful: ${successful}`);
        console.log(`❌ Failed: ${failed}`);
        console.log(`📊 Total: ${this.testResults.length}`);

        if (failed > 0) {
            console.log("\n❌ Failed Tests:");
            this.testResults
                .filter(r => !r.success)
                .forEach(r => {
                    console.log(`   - ${r.request.description}`);
                    console.log(`     Error: ${r.error}`);
                });
        }

        console.log("\n" + "=" .repeat(50));
    }

    async disconnect() {
        if (this.connection) {
            await this.connection.stop();
            console.log("\n🔌 Disconnected from hub");
        }
    }
}

// Main execution
async function main() {
    console.log("🚀 ChatAgent Conversational AI Test Suite");
    console.log("=" .repeat(50));

    const tester = new ConversationalAITester();

    // Connect to hub
    const connected = await tester.connect();
    if (!connected) {
        console.error("Failed to establish connection. Exiting.");
        process.exit(1);
    }

    // Wait a bit for connection to stabilize
    await new Promise(resolve => setTimeout(resolve, 1000));

    // Run tests
    await tester.runTests();

    // Disconnect
    await tester.disconnect();

    console.log("\n✨ Testing complete!");
}

// Run if executed directly
if (require.main === module) {
    main().catch(err => {
        console.error("Fatal error:", err);
        process.exit(1);
    });
}

module.exports = { ConversationalAITester };