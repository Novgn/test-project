/**
 * Type definitions for the ChatAgent application
 * These types match the backend domain models
 */

/**
 * Represents an agent in the system
 */
export interface Agent {
  id: string;
  name: string;
  description: string;
  type: 'Coordinator' | 'Tool' | 'Specialist';
  capabilities: string[];
}

/**
 * Represents a message in the conversation
 */
export interface ChatMessage {
  content: string;
  role: 'user' | 'assistant' | 'system';
  timestamp: string;
  agentId?: string;
}

/**
 * Represents a conversation session
 */
export interface Conversation {
  sessionId: string;
  messages: ChatMessage[];
  createdAt: string;
  updatedAt: string;
}

/**
 * Represents the metadata returned with orchestration results
 */
export interface OrchestrationMetadata {
  orchestration_type: string;
  orchestration_path: string;
  agent_count: number;
  mcp_enabled: boolean;
  runtime: string;
  error?: string;
}

/**
 * Real-time chat events from SignalR
 */
export interface ChatEvent {
  type: 'message' | 'typing' | 'agent_status' | 'error';
  data: any;
  timestamp: string;
}

/**
 * Agent status for real-time updates
 */
export interface AgentStatus {
  agentId: string;
  agentName: string;
  status: 'idle' | 'processing' | 'completed' | 'error';
  currentTask?: string;
}