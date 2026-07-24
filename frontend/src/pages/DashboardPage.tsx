import { Link } from 'react-router-dom';
import { ProjectSelector } from '../components/ProjectSelector';
import { useAppStore } from '../store/useAppStore';

export default function DashboardPage() {
  const { selectedProjectId, setSelectedProjectId } = useAppStore();

  return (
    <div className="p-6 max-w-3xl mx-auto space-y-6">
      <h1 className="text-2xl font-semibold">SupportForge AI</h1>
      <ProjectSelector value={selectedProjectId} onChange={setSelectedProjectId} />
      <Link
        to="/query"
        className="inline-block bg-blue-600 text-white px-4 py-2 rounded disabled:opacity-50"
        aria-disabled={!selectedProjectId}
      >
        New Query
      </Link>
    </div>
  );
}
