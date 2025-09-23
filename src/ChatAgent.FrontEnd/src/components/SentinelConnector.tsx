/**
 * Sentinel Connector Setup Component
 * Provides UI for setting up AWS-Azure Sentinel connector with real-time agent updates
 */

import React, { useState, useEffect, useRef } from 'react';
import signalRService from '../services/signalr';
import sentinelApi from '../services/sentinelApi';
import './SentinelConnector.css';

interface SetupConfiguration {
  workspaceId: string;
  tenantId: string;
  subscriptionId: string;
  resourceGroupName: string;
  logTypes: string[];
  awsRegion: string;
}

interface AgentMessage {
  agent: string;
  message: string;
  phase: string;
  timestamp: string;
}

interface SetupPhase {
  name: string;
  status: 'pending' | 'in-progress' | 'completed' | 'failed';
  messages: AgentMessage[];
}

const SentinelConnector: React.FC = () => {
  // Form state
  const [config, setConfig] = useState<SetupConfiguration>({
    workspaceId: '',
    tenantId: '',
    subscriptionId: '',
    resourceGroupName: '',
    logTypes: ['CloudTrail', 'VPCFlow', 'GuardDuty'],
    awsRegion: 'us-east-1'
  });

  // Setup state
  const [isSetupRunning, setIsSetupRunning] = useState(false);
  const [setupSessionId, setSetupSessionId] = useState<string>('');
  const [phases, setPhases] = useState<Map<string, SetupPhase>>(new Map([
    ['validation', { name: 'Prerequisites Validation', status: 'pending', messages: [] }],
    ['aws-setup', { name: 'AWS Infrastructure Setup', status: 'pending', messages: [] }],
    ['azure-setup', { name: 'Azure Sentinel Configuration', status: 'pending', messages: [] }],
    ['integration', { name: 'Integration & Connection', status: 'pending', messages: [] }],
    ['monitoring', { name: 'Monitoring & Verification', status: 'pending', messages: [] }],
    ['completion', { name: 'Final Report', status: 'pending', messages: [] }]
  ]));

  // Results state
  const [setupResult, setSetupResult] = useState<any>(null);
  const [currentPhase, setCurrentPhase] = useState<string>('');

  // Real-time messages
  const [agentMessages, setAgentMessages] = useState<AgentMessage[]>([]);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  // SignalR connection state (removed unused variable)

  useEffect(() => {
    // Setup SignalR listeners
    setupSignalRListeners();

    return () => {
      // Cleanup
      if (setupSessionId) {
        signalRService.leaveGroup(setupSessionId);
      }
    };
  }, [setupSessionId]);

  useEffect(() => {
    // Auto-scroll to latest message
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [agentMessages]);

  const setupSignalRListeners = () => {
    // Use the generic event handler for custom Sentinel events
    (signalRService as any).on('setupStarted', (data: any) => {
      console.log('Setup started:', data);
    });

    (signalRService as any).on('phaseUpdate', (data: any) => {
      const { phase } = data;
      setCurrentPhase(phase);
      updatePhaseStatus(phase, 'in-progress');

      // Mark previous phases as completed
      const phaseOrder = ['validation', 'aws-setup', 'azure-setup', 'integration', 'monitoring', 'completion'];
      const currentIndex = phaseOrder.indexOf(phase);
      for (let i = 0; i < currentIndex; i++) {
        updatePhaseStatus(phaseOrder[i], 'completed');
      }
    });

    (signalRService as any).on('agentMessage', (data: AgentMessage) => {
      setAgentMessages(prev => [...prev, data]);

      // Add message to appropriate phase
      setPhases(prev => {
        const newPhases = new Map(prev);
        const phase = newPhases.get(data.phase);
        if (phase) {
          phase.messages.push(data);
          newPhases.set(data.phase, { ...phase });
        }
        return newPhases;
      });
    });

    (signalRService as any).on('setupCompleted', (data: any) => {
      setIsSetupRunning(false);
      setSetupResult(data);
      updatePhaseStatus('completion', 'completed');
    });

    (signalRService as any).on('setupError', (data: any) => {
      setIsSetupRunning(false);
      updatePhaseStatus(currentPhase || 'validation', 'failed');
      console.error('Setup error:', data);
    });
  };

  const updatePhaseStatus = (phaseName: string, status: SetupPhase['status']) => {
    setPhases(prev => {
      const newPhases = new Map(prev);
      const phase = newPhases.get(phaseName);
      if (phase) {
        newPhases.set(phaseName, { ...phase, status });
      }
      return newPhases;
    });
  };

  const handleInputChange = (field: keyof SetupConfiguration, value: any) => {
    setConfig(prev => ({
      ...prev,
      [field]: value
    }));
  };

  const handleLogTypeToggle = (logType: string) => {
    setConfig(prev => ({
      ...prev,
      logTypes: prev.logTypes.includes(logType)
        ? prev.logTypes.filter(t => t !== logType)
        : [...prev.logTypes, logType]
    }));
  };

  const validateConfiguration = (): boolean => {
    return !!(
      config.workspaceId &&
      config.tenantId &&
      config.subscriptionId &&
      config.resourceGroupName &&
      config.logTypes.length > 0 &&
      config.awsRegion
    );
  };

  const startSetup = async () => {
    if (!validateConfiguration()) {
      alert('Please fill in all required fields');
      return;
    }

    setIsSetupRunning(true);
    setAgentMessages([]);
    setSetupResult(null);

    // Reset phases
    setPhases(new Map([
      ['validation', { name: 'Prerequisites Validation', status: 'pending', messages: [] }],
      ['aws-setup', { name: 'AWS Infrastructure Setup', status: 'pending', messages: [] }],
      ['azure-setup', { name: 'Azure Sentinel Configuration', status: 'pending', messages: [] }],
      ['integration', { name: 'Integration & Connection', status: 'pending', messages: [] }],
      ['monitoring', { name: 'Monitoring & Verification', status: 'pending', messages: [] }],
      ['completion', { name: 'Final Report', status: 'pending', messages: [] }]
    ]));

    try {
      // Start the setup via API
      const response = await sentinelApi.startSetup(config);
      setSetupSessionId(response.sessionId);

      // Join the SignalR group for this setup session
      await signalRService.joinGroup(response.sessionId);
    } catch (error) {
      console.error('Failed to start setup:', error);
      setIsSetupRunning(false);
    }
  };

  const getPhaseIcon = (status: SetupPhase['status']) => {
    switch (status) {
      case 'completed': return '✅';
      case 'in-progress': return '🔄';
      case 'failed': return '❌';
      default: return '⏳';
    }
  };

  return (
    <div className="sentinel-connector-container">
      <div className="connector-header">
        <h1>AWS-Azure Sentinel Connector Setup</h1>
        <p>Configure and deploy AWS log ingestion to Azure Sentinel using multi-agent orchestration</p>
      </div>

      <div className="connector-content">
        {/* Configuration Form */}
        <div className="config-section">
          <h2>Configuration</h2>

          <div className="form-group">
            <label>Azure Subscription ID *</label>
            <input
              type="text"
              value={config.subscriptionId}
              onChange={(e) => handleInputChange('subscriptionId', e.target.value)}
              placeholder="12345678-1234-1234-1234-123456789012"
              disabled={isSetupRunning}
            />
          </div>

          <div className="form-group">
            <label>Azure Tenant ID *</label>
            <input
              type="text"
              value={config.tenantId}
              onChange={(e) => handleInputChange('tenantId', e.target.value)}
              placeholder="87654321-4321-4321-4321-210987654321"
              disabled={isSetupRunning}
            />
          </div>

          <div className="form-group">
            <label>Sentinel Workspace ID *</label>
            <input
              type="text"
              value={config.workspaceId}
              onChange={(e) => handleInputChange('workspaceId', e.target.value)}
              placeholder="workspace-123456"
              disabled={isSetupRunning}
            />
          </div>

          <div className="form-group">
            <label>Resource Group Name *</label>
            <input
              type="text"
              value={config.resourceGroupName}
              onChange={(e) => handleInputChange('resourceGroupName', e.target.value)}
              placeholder="my-resource-group"
              disabled={isSetupRunning}
            />
          </div>

          <div className="form-group">
            <label>AWS Region *</label>
            <select
              value={config.awsRegion}
              onChange={(e) => handleInputChange('awsRegion', e.target.value)}
              disabled={isSetupRunning}
            >
              <option value="us-east-1">US East (N. Virginia)</option>
              <option value="us-west-2">US West (Oregon)</option>
              <option value="eu-west-1">EU (Ireland)</option>
              <option value="ap-southeast-1">Asia Pacific (Singapore)</option>
            </select>
          </div>

          <div className="form-group">
            <label>Log Types to Ingest *</label>
            <div className="log-types">
              {['CloudTrail', 'VPCFlow', 'GuardDuty', 'CloudWatch'].map(logType => (
                <label key={logType} className="checkbox-label">
                  <input
                    type="checkbox"
                    checked={config.logTypes.includes(logType)}
                    onChange={() => handleLogTypeToggle(logType)}
                    disabled={isSetupRunning}
                  />
                  {logType}
                </label>
              ))}
            </div>
          </div>

          <button
            className="setup-button"
            onClick={startSetup}
            disabled={isSetupRunning || !validateConfiguration()}
          >
            {isSetupRunning ? 'Setup in Progress...' : 'Start Setup'}
          </button>
        </div>

        {/* Setup Progress */}
        <div className="progress-section">
          <h2>Setup Progress</h2>

          <div className="phases-timeline">
            {Array.from(phases.entries()).map(([key, phase]) => (
              <div key={key} className={`phase-item ${phase.status}`}>
                <div className="phase-header">
                  <span className="phase-icon">{getPhaseIcon(phase.status)}</span>
                  <span className="phase-name">{phase.name}</span>
                </div>
                {phase.messages.length > 0 && (
                  <div className="phase-messages">
                    {phase.messages.slice(-3).map((msg, idx) => (
                      <div key={idx} className="phase-message">
                        <span className="agent-name">[{msg.agent}]</span>
                        <span className="message-text">{msg.message}</span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>

          {/* Real-time Agent Messages */}
          <div className="agent-messages-section">
            <h3>Agent Activity Log</h3>
            <div className="messages-log">
              {agentMessages.map((msg, idx) => (
                <div key={idx} className="log-message">
                  <span className="timestamp">
                    {new Date(msg.timestamp).toLocaleTimeString()}
                  </span>
                  <span className={`agent-badge ${msg.agent.toLowerCase().replace(' ', '-')}`}>
                    {msg.agent}
                  </span>
                  <span className="message-content">{msg.message}</span>
                </div>
              ))}
              <div ref={messagesEndRef} />
            </div>
          </div>

          {/* Setup Results */}
          {setupResult && (
            <div className="results-section">
              <h3>Setup Results</h3>
              <div className={`result-card ${setupResult.success ? 'success' : 'error'}`}>
                <h4>{setupResult.success ? '✅ Setup Completed Successfully' : '❌ Setup Failed'}</h4>
                {setupResult.connectorId && (
                  <div className="result-item">
                    <strong>Connector ID:</strong> {setupResult.connectorId}
                  </div>
                )}
                {setupResult.roleArn && (
                  <div className="result-item">
                    <strong>AWS Role ARN:</strong> {setupResult.roleArn}
                  </div>
                )}
                {setupResult.sqsUrls && setupResult.sqsUrls.length > 0 && (
                  <div className="result-item">
                    <strong>SQS Queue URLs:</strong>
                    <ul>
                      {setupResult.sqsUrls.map((url: string, idx: number) => (
                        <li key={idx}>{url}</li>
                      ))}
                    </ul>
                  </div>
                )}
                <p className="result-message">{setupResult.message}</p>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default SentinelConnector;