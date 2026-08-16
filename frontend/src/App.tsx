import { Routes, Route, Navigate } from 'react-router-dom';
import { ErrorBoundary } from './components/ErrorBoundary';
import { RequireAuth } from './components/RequireAuth';
import Layout from './components/Layout';
import DashboardPage from './pages/DashboardPage';
import ChatPage from './pages/ChatPage';
import SettingsProjectsPage from './pages/SettingsProjectsPage';
import SettingsEmployeesPage from './pages/SettingsEmployeesPage';
import SettingsConnectedAppsPage from './pages/SettingsConnectedAppsPage';
import SettingsFeedbackPage from './pages/SettingsFeedbackPage';
import SettingsTokenUsagePage from './pages/SettingsTokenUsagePage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import EscalationQueuePage from './pages/EscalationQueuePage';
import MyIssuesPage from './pages/MyIssuesPage';
import HelpPage from './pages/HelpPage';
import AccountPage from './pages/AccountPage';

export default function App() {
  return (
    <ErrorBoundary>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        <Route element={<RequireAuth />}>
          <Route element={<Layout />}>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/query" element={<ChatPage />} />
            {/* U3: /admin split into routed /settings/* sub-pages; kept as a redirect (not removed)
                since it was the long-standing bookmarked/linked path. */}
            <Route path="/admin" element={<Navigate to="/settings/projects" replace />} />
            <Route path="/settings" element={<Navigate to="/settings/projects" replace />} />
            <Route path="/settings/projects" element={<SettingsProjectsPage />} />
            <Route path="/settings/employees" element={<SettingsEmployeesPage />} />
            <Route path="/settings/connected-apps" element={<SettingsConnectedAppsPage />} />
            <Route path="/settings/feedback" element={<SettingsFeedbackPage />} />
            <Route path="/settings/token-usage" element={<SettingsTokenUsagePage />} />
            {/* U21: server-side gate is GET /api/escalations' [Authorize(Roles="L2,L3,Admin")] --
                the page itself renders empty/403-from-API for an L1 session that navigates here
                directly; Layout's nav (client-side mirror, same RequireRole convention as
                EmployeesSection) is what actually hides the link for L1. */}
            <Route path="/escalations" element={<EscalationQueuePage />} />
            <Route path="/my-issues" element={<MyIssuesPage />} />
            <Route path="/help" element={<HelpPage />} />
            <Route path="/account" element={<AccountPage />} />
          </Route>
        </Route>
      </Routes>
    </ErrorBoundary>
  );
}
