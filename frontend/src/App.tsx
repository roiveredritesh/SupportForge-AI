import { Routes, Route } from 'react-router-dom';
import DashboardPage from './pages/DashboardPage';
import QueryPage from './pages/QueryPage';

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/query" element={<QueryPage />} />
    </Routes>
  );
}
