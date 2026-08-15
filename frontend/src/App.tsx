import { Routes, Route } from 'react-router-dom';
import { ErrorBoundary } from './components/ErrorBoundary';
import { RequireAuth } from './components/RequireAuth';
import Layout from './components/Layout';
import DashboardPage from './pages/DashboardPage';
import ChatPage from './pages/ChatPage';
import AdminPage from './pages/AdminPage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import EscalationQueuePage from './pages/EscalationQueuePage';
import MyIssuesPage from './pages/MyIssuesPage';

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
            <Route path="/admin" element={<AdminPage />} />
            {/* U21: server-side gate is GET /api/escalations' [Authorize(Roles="L2,L3,Admin")] --
                the page itself renders empty/403-from-API for an L1 session that navigates here
                directly; Layout's nav (client-side mirror, same RequireRole convention as
                EmployeesSection) is what actually hides the link for L1. */}
            <Route path="/escalations" element={<EscalationQueuePage />} />
            <Route path="/my-issues" element={<MyIssuesPage />} />
          </Route>
        </Route>
      </Routes>
    </ErrorBoundary>
  );
}
