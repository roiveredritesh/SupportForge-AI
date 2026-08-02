import { Routes, Route } from 'react-router-dom';
import { ErrorBoundary } from './components/ErrorBoundary';
import { RequireAuth } from './components/RequireAuth';
import Layout from './components/Layout';
import DashboardPage from './pages/DashboardPage';
import ChatPage from './pages/ChatPage';
import AdminPage from './pages/AdminPage';
import LoginPage from './pages/LoginPage';

export default function App() {
  return (
    <ErrorBoundary>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<RequireAuth />}>
          <Route element={<Layout />}>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/query" element={<ChatPage />} />
            <Route path="/admin" element={<AdminPage />} />
          </Route>
        </Route>
      </Routes>
    </ErrorBoundary>
  );
}
