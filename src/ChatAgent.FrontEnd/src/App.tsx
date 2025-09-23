/**
 * Main App component
 * Integrates the ChatAgent orchestrator UI with routing
 */

import { BrowserRouter as Router, Routes, Route, Link } from 'react-router-dom'
import Chat from './components/Chat'
import SentinelConnector from './components/SentinelConnector'
import './App.css'

function App() {
  return (
    <Router>
      <div className="app">
        <nav className="app-nav">
          <div className="nav-brand">
            <h1>ChatAgent Platform</h1>
          </div>
          <div className="nav-links">
            <Link to="/" className="nav-link">Chat</Link>
            <Link to="/sentinel-connector" className="nav-link">Sentinel Connector</Link>
          </div>
        </nav>

        <main className="app-main">
          <Routes>
            <Route path="/" element={<Chat />} />
            <Route path="/sentinel-connector" element={<SentinelConnector />} />
          </Routes>
        </main>
      </div>
    </Router>
  )
}

export default App