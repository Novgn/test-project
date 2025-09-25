/**
 * Main App component
 * Integrates the ChatAgent orchestrator UI with routing
 */

import Chat from './components/Chat'
import './App.css'

function App() {
  return (
    <div className="app">
      <main className="app-main">
        <Chat />
      </main>
    </div>
  )
}

export default App