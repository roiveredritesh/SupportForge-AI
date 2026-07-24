import { Routes, Route } from 'react-router-dom';
import { ErrorBoundary } from './components/ErrorBoundary';
import { ThemeToggle } from './components/ThemeToggle';
import DashboardPage from './pages/DashboardPage';
import QueryPage from './pages/QueryPage';
import AdminPage from './pages/AdminPage';

export default function App() {
  return (
    <ErrorBoundary>
      <div className="min-h-screen dark:bg-gray-900 dark:text-gray-100">
        <div className="flex justify-end p-2">
          <ThemeToggle />
        </div>
        <Routes>
          <Route path="/" element={<DashboardPage />} />
          <Route path="/query" element={<QueryPage />} />
          <Route path="/admin" element={<AdminPage />} />
        </Routes>
      </div>
    </ErrorBoundary>
  );
}
