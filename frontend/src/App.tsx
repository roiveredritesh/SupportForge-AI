import { Routes, Route } from 'react-router-dom';
import DashboardPage from './pages/DashboardPage';
import QueryPage from './pages/QueryPage';
import AdminPage from './pages/AdminPage';

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/query" element={<QueryPage />} />
      <Route path="/admin" element={<AdminPage />} />
    </Routes>
  );
}
