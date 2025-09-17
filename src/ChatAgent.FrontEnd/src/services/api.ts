/**
 * API Service for communicating with the ChatAgent backend
 * Handles all HTTP requests to the orchestrator endpoints
 */

import axios from 'axios';
import type { AxiosInstance } from 'axios';
import type { Agent, ChatMessage, Conversation } from '../types';

/**
 * Configuration for the API service
 */
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'https://localhost:7248';

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
   * Get all available agents with their capabilities
   */
  async getAgents(): Promise<Agent[]> {
    const response = await apiClient.get<Agent[]>('/api/agents');
    return response.data;
  },

  /**
   * Get a specific agent by ID
   */
  async getAgent(agentId: string): Promise<Agent> {
    const response = await apiClient.get<Agent>(`/api/agents/${agentId}`);
    return response.data;
  },

  /**
   * Send a message to the orchestrator and get a response
   */
  async sendMessage(sessionId: string, message: string): Promise<ChatMessage> {
    const response = await apiClient.post<ChatMessage>('/api/chat', {
      sessionId,
      message,
    });
    return response.data;
  },

  /**
   * Get conversation history for a session
   */
  async getConversation(sessionId: string): Promise<Conversation> {
    const response = await apiClient.get<Conversation>(`/api/sessions/${sessionId}`);
    return response.data;
  },

  /**
   * Create a new conversation session
   */
  async createSession(): Promise<string> {
    const response = await apiClient.post<{ id: string }>('/api/sessions');
    return response.data.id;
  },

  /**
   * Clear conversation history for a session
   */
  async clearConversation(sessionId: string): Promise<void> {
    await apiClient.put(`/api/sessions/${sessionId}/end`);
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