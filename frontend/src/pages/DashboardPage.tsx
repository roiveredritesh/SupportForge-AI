import { Link } from 'react-router-dom';
import { ProjectSelector } from '../components/ProjectSelector';
import { useAppStore } from '../store/useAppStore';

export default function DashboardPage() {
  const { selectedProjectId, setSelectedProjectId } = useAppStore();

  return (
    <div className="mx-auto max-w-3xl space-y-6 p-6">
      <div>
        <h1 className="text-2xl font-semibold">Dashboard</h1>
        <p className="text-sm text-slate-500 dark:text-gray-400">
          Pick a project, then ask the AI agent or manage its knowledge base.
        </p>
      </div>

      <div className="rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <p className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-gray-500">
          Project
        </p>
        <ProjectSelector value={selectedProjectId} onChange={setSelectedProjectId} />
      </div>

      <div className="flex gap-3">
        {selectedProjectId ? (
          <Link
            to="/query"
            className="inline-block rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700"
          >
            New Query
          </Link>
        ) : (
          <span
            className="inline-block cursor-not-allowed rounded-lg bg-indigo-600 px-4 py-2 text-white opacity-50"
            title="Select a project first"
          >
            New Query
          </span>
        )}
        <Link
          to="/settings"
          className="inline-block rounded-lg border border-slate-300 px-4 py-2 text-slate-700 hover:bg-slate-100 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
        >
          Settings
        </Link>
      </div>
    </div>
  );
}
