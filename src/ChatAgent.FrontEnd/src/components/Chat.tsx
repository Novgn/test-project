/**
 * Main Chat component for interacting with the ChatAgent orchestrator
 * Provides the user interface for sending messages and viewing responses
 */

import React, { useState, useEffect, useRef } from 'react';
import type { ChatMessage, Agent, AgentStatus } from '../types';
import api from '../services/api';
import signalRService from '../services/signalr';
import './Chat.css';

/**
 * Chat Component - Main interface for the orchestrator
 */
export const Chat: React.FC = () => {
  // State management
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [inputMessage, setInputMessage] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [sessionId, setSessionId] = useState<string>('');
  const [agents, setAgents] = useState<Agent[]>([]);
  const [activeAgents, setActiveAgents] = useState<Map<string, AgentStatus>>(new Map());
  const [isConnected, setIsConnected] = useState(false);
  const [typingAgents, setTypingAgents] = useState<Set<string>>(new Set());

  // Refs for auto-scrolling
  const messagesEndRef = useRef<HTMLDivElement>(null);

  // Initialize session and load agents on mount
  useEffect(() => {
    initializeChat();
    return () => {
      // Cleanup on unmount
      signalRService.disconnect();
    };
  }, []);

  // Auto-scroll to bottom when messages change
  useEffect(() => {
    scrollToBottom();
  }, [messages]);

  /**
   * Initialize chat session and connections
   */
  const initializeChat = async () => {
    try {
      // Create or get session ID
      let storedSessionId = localStorage.getItem('chatSessionId');
      if (!storedSessionId) {
        storedSessionId = await api.createSession();
        localStorage.setItem('chatSessionId', storedSessionId);
      }
      setSessionId(storedSessionId);

      // Load available agents (optional - may not exist yet)
      try {
        const agentsList = await api.getAgents();
        setAgents(agentsList);
      } catch (error) {
        console.log('Agents endpoint not available yet');
      }

      // Load conversation history
      try {
        const sessionData = await api.getConversation(storedSessionId);
        if (sessionData && sessionData.messages) {
          setMessages(sessionData.messages);
        }
      } catch (error) {
        console.log('No existing conversation for session:', storedSessionId);
      }

      // Setup SignalR connection
      await setupSignalR(storedSessionId);
    } catch (error) {
      console.error('Failed to initialize chat:', error);
    }
  };

  /**
   * Setup SignalR connection and event handlers
   */
  const setupSignalR = async (sessionId: string) => {
    try {
      // Register event handlers
      signalRService.on('messageReceived', handleMessageReceived);
      signalRService.on('agentStatusChanged', handleAgentStatusChanged);
      signalRService.on('typingIndicator', handleTypingIndicator);
      signalRService.on('error', handleError);
      signalRService.on('connected', () => setIsConnected(true));
      signalRService.on('disconnected', () => setIsConnected(false));

      // Connect to SignalR (session managed on server side)
      await signalRService.connect();
    } catch (error) {
      console.error('SignalR setup failed:', error);
    }
  };

  /**
   * Handle incoming messages from SignalR
   */
  const handleMessageReceived = (message: ChatMessage) => {
    setMessages(prev => [...prev, message]);
    setTypingAgents(new Set()); // Clear typing indicators
  };

  /**
   * Handle agent status changes
   */
  const handleAgentStatusChanged = (status: AgentStatus) => {
    setActiveAgents(prev => {
      const newMap = new Map(prev);
      if (status.status === 'idle') {
        newMap.delete(status.agentId);
      } else {
        newMap.set(status.agentId, status);
      }
      return newMap;
    });
  };

  /**
   * Handle typing indicators
   */
  const handleTypingIndicator = (agentId: string, isTyping: boolean) => {
    setTypingAgents(prev => {
      const newSet = new Set(prev);
      if (isTyping) {
        newSet.add(agentId);
      } else {
        newSet.delete(agentId);
      }
      return newSet;
    });
  };

  /**
   * Handle errors from SignalR
   */
  const handleError = (error: string) => {
    console.error('SignalR error:', error);
    // Could show a toast notification here
  };

  /**
   * Send a message to the orchestrator
   */
  const sendMessage = async () => {
    if (!inputMessage.trim() || isLoading) return;

    const userMessage: ChatMessage = {
      content: inputMessage,
      role: 'user',
      timestamp: new Date().toISOString(),
    };

    // Add user message to chat immediately
    setMessages(prev => [...prev, userMessage]);
    setInputMessage('');
    setIsLoading(true);

    try {
      // Send message via SignalR if connected, otherwise use API
      if (signalRService.getIsConnected()) {
        await signalRService.sendMessage(inputMessage);
      } else {
        // Fallback to API if SignalR is not connected
        const response = await api.sendMessage(sessionId, inputMessage);
        setMessages(prev => [...prev, response]);
      }
    } catch (error) {
      console.error('Failed to send message:', error);
      const errorMessage: ChatMessage = {
        content: 'Failed to send message. Please try again.',
        role: 'system',
        timestamp: new Date().toISOString(),
      };
      setMessages(prev => [...prev, errorMessage]);
    } finally {
      setIsLoading(false);
    }
  };

  /**
   * Clear conversation history
   */
  const clearConversation = async () => {
    try {
      await api.clearConversation(sessionId);
      setMessages([]);

      // Create new session
      const newSessionId = await api.createSession();
      localStorage.setItem('chatSessionId', newSessionId);
      setSessionId(newSessionId);

      // Reconnect SignalR with new session
      await signalRService.disconnect();
      await setupSignalR(newSessionId);
    } catch (error) {
      console.error('Failed to clear conversation:', error);
    }
  };

  /**
   * Scroll to bottom of messages
   */
  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  /**
   * Format timestamp for display
   */
  const formatTime = (timestamp: string) => {
    const date = new Date(timestamp);
    return date.toLocaleTimeString('en-US', {
      hour: '2-digit',
      minute: '2-digit'
    });
  };

  /**
   * Get agent name from ID
   */
  const getAgentName = (agentId?: string) => {
    if (!agentId) return 'System';
    const agent = agents.find(a => a.id === agentId);
    return agent?.name || agentId;
  };

  return (
    <div className="chat-container">
      {/* Header */}
      <div className="chat-header">
        <h1>ChatAgent Orchestrator</h1>
        <div className="header-controls">
          <div className={`connection-status ${isConnected ? 'connected' : 'disconnected'}`}>
            {isConnected ? '● Connected' : '○ Disconnected'}
          </div>
          <button onClick={clearConversation} className="clear-button">
            Clear Chat
          </button>
        </div>
      </div>

      {/* Agent Status Bar */}
      {activeAgents.size > 0 && (
        <div className="agents-status-bar">
          {Array.from(activeAgents.values()).map(agent => (
            <div key={agent.agentId} className="agent-status">
              <span className="agent-name">{agent.agentName}</span>
              <span className="agent-task">{agent.currentTask || 'Processing...'}</span>
              <span className={`status-indicator ${agent.status}`}></span>
            </div>
          ))}
        </div>
      )}

      {/* Messages Area */}
      <div className="messages-container">
        {messages.map((message, index) => (
          <div key={index} className={`message ${message.role}`}>
            <div className="message-header">
              <span className="message-author">
                {message.role === 'user' ? 'You' : getAgentName(message.agentId)}
              </span>
              <span className="message-time">{formatTime(message.timestamp)}</span>
            </div>
            <div className="message-content">{message.content}</div>
          </div>
        ))}

        {/* Typing Indicators */}
        {typingAgents.size > 0 && (
          <div className="typing-indicators">
            {Array.from(typingAgents).map(agentId => (
              <div key={agentId} className="typing-indicator">
                {getAgentName(agentId)} is typing...
              </div>
            ))}
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      {/* Input Area */}
      <div className="input-container">
        <input
          type="text"
          value={inputMessage}
          onChange={(e) => setInputMessage(e.target.value)}
          onKeyPress={(e) => e.key === 'Enter' && sendMessage()}
          placeholder="Type your message..."
          disabled={isLoading || !isConnected}
          className="message-input"
        />
        <button
          onClick={sendMessage}
          disabled={isLoading || !isConnected || !inputMessage.trim()}
          className="send-button"
        >
          {isLoading ? 'Sending...' : 'Send'}
        </button>
      </div>

      {/* Agents List Sidebar */}
      <div className="agents-sidebar">
        <h3>Available Agents</h3>
        {agents.map(agent => (
          <div key={agent.id} className="agent-card">
            <div className="agent-header">
              <span className="agent-name">{agent.name}</span>
              <span className={`agent-type ${agent.type.toLowerCase()}`}>
                {agent.type}
              </span>
            </div>
            <div className="agent-capabilities">
              {agent.capabilities.map((cap, i) => (
                <span key={i} className="capability-badge">{cap}</span>
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};

export default Chat;