/**
 * Sentinel Connector API Service
 * Handles communication with the Sentinel connector backend endpoints
 */

const API_BASE_URL = import.meta.env.VITE_API_URL || 'http://localhost:5000';

interface SetupConfiguration {
  workspaceId: string;
  tenantId: string;
  subscriptionId: string;
  resourceGroupName: string;
  logTypes: string[];
  awsRegion: string;
}

interface SetupResponse {
  sessionId: string;
  message: string;
  signalRHub: string;
  joinGroup: string;
}

interface ConnectorStatus {
  connectorId: string;
  status: string;
  lastDataReceived: string;
  dataVolume: {
    last24Hours: string;
    last7Days: string;
    last30Days: string;
  };
  logTypes: Array<{
    type: string;
    status: string;
    recordsIngested: number;
  }>;
  health: {
    overall: string;
    authentication: string;
    dataIngestion: string;
    errors: number;
  };
}

class SentinelApi {
  /**
   * Start the Sentinel connector setup process
   */
  async startSetup(config: SetupConfiguration): Promise<SetupResponse> {
    const response = await fetch(`${API_BASE_URL}/api/SentinelConnector/setup`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(config),
    });

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Failed to start setup: ${error}`);
    }

    return response.json();
  }

  /**
   * Validate prerequisites for the setup
   */
  async validatePrerequisites(config: Partial<SetupConfiguration>): Promise<any> {
    const response = await fetch(`${API_BASE_URL}/api/SentinelConnector/validate`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(config),
    });

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Validation failed: ${error}`);
    }

    return response.json();
  }

  /**
   * Get the status of an existing connector
   */
  async getConnectorStatus(connectorId: string): Promise<ConnectorStatus> {
    const response = await fetch(`${API_BASE_URL}/api/SentinelConnector/status/${connectorId}`, {
      method: 'GET',
      headers: {
        'Accept': 'application/json',
      },
    });

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Failed to get connector status: ${error}`);
    }

    return response.json();
  }

  /**
   * Join a setup session for real-time updates
   */
  async joinSession(sessionId: string, connectionId: string): Promise<any> {
    const response = await fetch(
      `${API_BASE_URL}/api/SentinelConnector/join-session/${sessionId}?connectionId=${connectionId}`,
      {
        method: 'POST',
        headers: {
          'Accept': 'application/json',
        },
      }
    );

    if (!response.ok) {
      const error = await response.text();
      throw new Error(`Failed to join session: ${error}`);
    }

    return response.json();
  }
}

export default new SentinelApi();