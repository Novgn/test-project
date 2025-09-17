/**
 * SignalR Service for real-time communication with the ChatAgent backend
 * Handles WebSocket connections for live updates during orchestration
 */

import * as signalR from '@microsoft/signalr';
import type { ChatMessage, AgentStatus } from '../types';

/**
 * SignalR hub URL configuration
 */
const HUB_URL = import.meta.env.VITE_API_BASE_URL || 'https://localhost:7248';

/**
 * SignalR connection manager
 */
class SignalRService {
  private connection: signalR.HubConnection | null = null;
  private isConnected = false;
  private reconnectTimer: NodeJS.Timeout | null = null;

  /**
   * Event handlers for different message types
   */
  private handlers = {
    onMessageReceived: null as ((message: ChatMessage) => void) | null,
    onAgentStatusChanged: null as ((status: AgentStatus) => void) | null,
    onTypingIndicator: null as ((agentId: string, isTyping: boolean) => void) | null,
    onError: null as ((error: string) => void) | null,
    onConnected: null as (() => void) | null,
    onDisconnected: null as (() => void) | null,
  };

  /**
   * Initialize SignalR connection
   */
  async connect(): Promise<void> {
    if (this.connection) {
      await this.disconnect();
    }

    // Create new connection with automatic reconnection
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${HUB_URL}/chathub`)
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          // Exponential backoff: 0, 2, 4, 8, 16, 30 seconds
          if (retryContext.previousRetryCount === 0) return 0;
          if (retryContext.previousRetryCount === 1) return 2000;
          if (retryContext.previousRetryCount === 2) return 4000;
          if (retryContext.previousRetryCount === 3) return 8000;
          if (retryContext.previousRetryCount === 4) return 16000;
          return 30000;
        },
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    // Set up event handlers
    this.setupEventHandlers();

    // Start the connection
    try {
      await this.connection.start();
      this.isConnected = true;
      console.log('SignalR connected successfully');

      // Notify connected handler
      if (this.handlers.onConnected) {
        this.handlers.onConnected();
      }
    } catch (error) {
      console.error('SignalR connection failed:', error);
      this.scheduleReconnect();
      throw error;
    }
  }

  /**
   * Set up SignalR event handlers
   */
  private setupEventHandlers(): void {
    if (!this.connection) return;

    // Handle incoming messages
    this.connection.on('ReceiveMessage', (message: ChatMessage) => {
      console.log('Message received:', message);
      if (this.handlers.onMessageReceived) {
        this.handlers.onMessageReceived(message);
      }
    });

    // Handle agent status updates
    this.connection.on('AgentStatusChanged', (status: AgentStatus) => {
      console.log('Agent status changed:', status);
      if (this.handlers.onAgentStatusChanged) {
        this.handlers.onAgentStatusChanged(status);
      }
    });

    // Handle typing indicators
    this.connection.on('TypingIndicator', (agentId: string, isTyping: boolean) => {
      if (this.handlers.onTypingIndicator) {
        this.handlers.onTypingIndicator(agentId, isTyping);
      }
    });

    // Handle errors
    this.connection.on('Error', (error: string) => {
      console.error('SignalR error:', error);
      if (this.handlers.onError) {
        this.handlers.onError(error);
      }
    });

    // Handle Connected event from server
    this.connection.on('Connected', (data: any) => {
      console.log('Connected with session:', data.sessionId);
    });

    // Handle Processing event
    this.connection.on('Processing', (data: any) => {
      console.log('Processing message...');
    });

    // Handle connection state changes
    this.connection.onreconnecting(() => {
      console.log('SignalR reconnecting...');
      this.isConnected = false;
    });

    this.connection.onreconnected(() => {
      console.log('SignalR reconnected');
      this.isConnected = true;
      if (this.handlers.onConnected) {
        this.handlers.onConnected();
      }
    });

    this.connection.onclose(() => {
      console.log('SignalR disconnected');
      this.isConnected = false;
      if (this.handlers.onDisconnected) {
        this.handlers.onDisconnected();
      }
    });
  }

  /**
   * Schedule automatic reconnection
   */
  private scheduleReconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
    }

    this.reconnectTimer = setTimeout(async () => {
      console.log('Attempting to reconnect...');
      try {
        await this.connect();
      } catch (error) {
        console.error('Reconnection failed:', error);
        this.scheduleReconnect();
      }
    }, 5000); // Retry after 5 seconds
  }

  /**
   * Send a message through SignalR
   */
  async sendMessage(message: string): Promise<void> {
    if (!this.connection || !this.isConnected) {
      throw new Error('SignalR is not connected');
    }

    await this.connection.invoke('SendMessage', message);
  }

  /**
   * Disconnect from SignalR
   */
  async disconnect(): Promise<void> {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
      this.isConnected = false;
    }
  }

  /**
   * Check if connected
   */
  getIsConnected(): boolean {
    return this.isConnected;
  }

  /**
   * Register event handlers
   */
  on(event: 'messageReceived', handler: (message: ChatMessage) => void): void;
  on(event: 'agentStatusChanged', handler: (status: AgentStatus) => void): void;
  on(event: 'typingIndicator', handler: (agentId: string, isTyping: boolean) => void): void;
  on(event: 'error', handler: (error: string) => void): void;
  on(event: 'connected', handler: () => void): void;
  on(event: 'disconnected', handler: () => void): void;
  on(event: string, handler: any): void {
    switch (event) {
      case 'messageReceived':
        this.handlers.onMessageReceived = handler;
        break;
      case 'agentStatusChanged':
        this.handlers.onAgentStatusChanged = handler;
        break;
      case 'typingIndicator':
        this.handlers.onTypingIndicator = handler;
        break;
      case 'error':
        this.handlers.onError = handler;
        break;
      case 'connected':
        this.handlers.onConnected = handler;
        break;
      case 'disconnected':
        this.handlers.onDisconnected = handler;
        break;
    }
  }

  /**
   * Unregister event handlers
   */
  off(event: string): void {
    switch (event) {
      case 'messageReceived':
        this.handlers.onMessageReceived = null;
        break;
      case 'agentStatusChanged':
        this.handlers.onAgentStatusChanged = null;
        break;
      case 'typingIndicator':
        this.handlers.onTypingIndicator = null;
        break;
      case 'error':
        this.handlers.onError = null;
        break;
      case 'connected':
        this.handlers.onConnected = null;
        break;
      case 'disconnected':
        this.handlers.onDisconnected = null;
        break;
    }
  }
}

// Export singleton instance
export const signalRService = new SignalRService();
export default signalRService;