import { Center, Loader } from '@mantine/core'
import { Navigate, Route, Routes } from 'react-router-dom'
import { LiveProvider } from './api/live'
import { useAuth } from './auth'
import { Layout } from './components/Layout'
import { PageContextProvider } from './pageContext'
import { ApprovalsPage } from './pages/Approvals'
import { AssistantPage } from './pages/Assistant'
import { AutomationPage } from './pages/Automation'
import { BedPage } from './pages/BedDetail'
import { DashboardPage } from './pages/Dashboard'
import { DeviceDetailPage } from './pages/DeviceDetail'
import { DevicesPage } from './pages/Devices'
import { GardenPage } from './pages/Garden'
import { LoginPage } from './pages/Login'
import { McpDocsPage } from './pages/McpDocs'
import { LogsPage } from './pages/Logs'
import { NotificationsPage } from './pages/Notifications'
import { PlantPage } from './pages/PlantDetail'
import { ReportsPage } from './pages/Reports'
import { ReservoirPage } from './pages/ReservoirDetail'
import { SafetyPage } from './pages/Safety'
import { SensorsPage } from './pages/Sensors'
import { SettingsPage } from './pages/Settings'

export default function App() {
  const { phase, statusError } = useAuth()
  if (phase === 'loading') {
    return (
      <Center h="100vh">
        <Loader />
      </Center>
    )
  }
  if (phase !== 'app') return <LoginPage phase={phase} statusError={statusError} />

  return (
    <LiveProvider>
      <PageContextProvider>
        <Layout>
          <Routes>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/garden" element={<GardenPage />} />
            <Route path="/beds/:id" element={<BedPage />} />
            <Route path="/plants/:id" element={<PlantPage />} />
            <Route path="/reservoirs/:id" element={<ReservoirPage />} />
            <Route path="/sensors" element={<SensorsPage />} />
            <Route path="/devices" element={<DevicesPage />} />
            <Route path="/devices/:id" element={<DeviceDetailPage />} />
            <Route path="/reports" element={<ReportsPage />} />
            <Route path="/logs" element={<LogsPage />} />
            <Route path="/automation" element={<AutomationPage />} />
            <Route path="/safety" element={<SafetyPage />} />
            <Route path="/assistant" element={<AssistantPage />} />
            <Route path="/assistant/:id" element={<AssistantPage />} />
            <Route path="/approvals" element={<ApprovalsPage />} />
            <Route path="/notifications" element={<NotificationsPage />} />
            <Route path="/settings/*" element={<SettingsPage />} />
            <Route path="/mcp-tools" element={<McpDocsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Layout>
      </PageContextProvider>
    </LiveProvider>
  )
}
