/**
 * API Service for communicating with the Sentinel Connector backend
 * Handles all HTTP requests for conversational deployment assistance
 */

import axios from 'axios';
import type { AxiosInstance } from 'axios';
import type { Agent, ChatMessage, Conversation } from '../types';

/**
 * Configuration for the API service
 * In development, use relative URLs to go through Vite proxy
 * In production, use the environment variable or default
 */
const API_BASE_URL = import.meta.env.DEV
  ? '' // Empty string for relative URLs in development (uses Vite proxy)
  : (import.meta.env.VITE_API_BASE_URL || 'https://localhost:7248');

/**
 * Create axios instance with default configuration
 */
const apiClient: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  headers: {
    'Content-Type': 'application/json',
  },
  // Allow self-signed certificates in development
  ...(import.meta.env.DEV && {
    httpsAgent: {
      rejectUnauthorized: false,
    },
  }),
});

/**
 * Request interceptor for adding auth tokens if needed
 */
apiClient.interceptors.request.use(
  (config) => {
    // Add auth token if available
    const token = localStorage.getItem('authToken');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

/**
 * Response interceptor for handling errors globally
 */
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response?.status === 401) {
      // Handle unauthorized access
      console.error('Unauthorized access - redirecting to login');
      // Could redirect to login page here
    }
    return Promise.reject(error);
  }
);

/**
 * API methods for the ChatAgent service
 */
export const api = {
  /**
   * Get all available specialists and their roles
   */
  async getAgents(): Promise<Agent[]> {
    try {
      interface SpecialistResponse {
        name: string;
        role: string;
        capabilities?: string[];
        available?: boolean;
      }
      const response = await apiClient.get<SpecialistResponse[]>('/api/SentinelConnector/specialists');
      // Map the response to Agent type
      return response.data.map(specialist => ({
        id: specialist.name.toLowerCase().replace(' ', ''),
        name: specialist.name,
        type: 'Specialist' as const,
        description: specialist.role,
        capabilities: specialist.capabilities || []
      }));
    } catch (error) {
      console.error('Failed to get specialists:', error);
      // Return default specialists if API fails
      return [
        { id: 'coordinator', name: 'Coordinator', type: 'Coordinator', description: 'Guides the setup process', capabilities: [] },
        { id: 'aws', name: 'AWS Expert', type: 'Specialist', description: 'AWS infrastructure', capabilities: [] },
        { id: 'azure', name: 'Azure Specialist', type: 'Specialist', description: 'Azure Sentinel configuration', capabilities: [] },
      ];
    }
  },

  /**
   * Get a specific agent by ID
   */
  async getAgent(agentId: string): Promise<Agent> {
    const response = await apiClient.get<Agent>(`/api/agents/${agentId}`);
    return response.data;
  },

  /**
   * Send a conversational message to the assistant
   */
  async sendMessage(sessionId: string, message: string): Promise<ChatMessage> {
    try {
      interface ChatResponse {
        message?: string;
        response?: string;
        speaker?: string;
        agent?: string;
        timestamp?: string;
      }
      const response = await apiClient.post<ChatResponse>(`/api/SentinelConnector/chat/${sessionId}`, {
        message,
      });

      // Map response to ChatMessage type
      return {
        content: response.data.message || response.data.response || '',
        role: 'assistant',
        agentId: response.data.speaker || response.data.agent,
        timestamp: response.data.timestamp || new Date().toISOString(),
      };
    } catch (error) {
      console.error('Failed to send message:', error);
      throw error;
    }
  },

  /**
   * Get conversation history for a session
   */
  async getConversation(sessionId: string): Promise<Conversation> {
    try {
      interface HistoryResponse {
        sessionId: string;
        messages: Array<{
          message?: string;
          content?: string;
          isUser?: boolean;
          speaker?: string;
          timestamp: string;
        }>;
      }
      const response = await apiClient.get<HistoryResponse>(`/api/SentinelConnector/session/${sessionId}/history`);

      // Map response to Conversation type
      return {
        sessionId: response.data.sessionId,
        messages: response.data.messages.map((msg) => ({
          content: msg.message || msg.content || '',
          role: msg.isUser ? 'user' : 'assistant' as const,
          agentId: msg.speaker === 'You' ? undefined : msg.speaker,
          timestamp: msg.timestamp
        })),
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      };
    } catch (error) {
      console.error('Failed to get conversation:', error);
      return {
        sessionId,
        messages: [],
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      };
    }
  },

  /**
   * Create a new conversation session
   */
  async createSession(): Promise<string> {
    // For now, just generate a local session ID
    // The backend will create the actual session when the first message is sent
    const sessionId = 'session-' + Date.now();
    console.log('Created new session:', sessionId);
    return sessionId;
  },

  /**
   * Clear conversation history for a session
   */
  async clearConversation(sessionId: string): Promise<void> {
    try {
      await apiClient.put(`/api/sessions/${sessionId}/end`);
    } catch (error) {
      console.error('Failed to clear conversation:', error);
      // Continue anyway - we'll create a new session
    }
  },

  /**
   * Test connection to the API
   */
  async healthCheck(): Promise<boolean> {
    try {
      await apiClient.get('/');
      return true;
    } catch {
      return false;
    }
  },
};

export default api;