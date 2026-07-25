import { useState } from 'react';
import { useProjects } from '../hooks/useProjects';
import { useCreateProject } from '../hooks/useCreateProject';
import { useTriggerIngestion } from '../hooks/useTriggerIngestion';

export default function AdminPage() {
  const { data: projects } = useProjects();
  const createProject = useCreateProject();
  const triggerIngestion = useTriggerIngestion();

  const [id, setId] = useState('');
  const [name, setName] = useState('');
  const [repoOwner, setRepoOwner] = useState('');
  const [repoName, setRepoName] = useState('');
  const [kbFolder, setKbFolder] = useState('');

  const handleCreate = () => {
    createProject.mutate({
      id,
      name,
      repos: repoOwner && repoName ? [{ owner: repoOwner, repo: repoName, defaultBranch: 'main' }] : [],
      kbSources: kbFolder ? [{ type: 'Documents', location: kbFolder }] : [],
    });
  };

  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <h1 className="text-xl font-semibold">Project Administration</h1>

      <section className="space-y-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Add Project</h2>
        <label className="block text-sm">Project ID
          <input className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900" value={id} onChange={(e) => setId(e.target.value)} />
        </label>
        <label className="block text-sm">Project Name
          <input className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900" value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="block text-sm">GitHub Repo Owner
          <input className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900" value={repoOwner} onChange={(e) => setRepoOwner(e.target.value)} />
        </label>
        <label className="block text-sm">GitHub Repo Name
          <input className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900" value={repoName} onChange={(e) => setRepoName(e.target.value)} />
        </label>
        <label className="block text-sm">KB Documents Folder
          <input className="mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900" value={kbFolder} onChange={(e) => setKbFolder(e.target.value)} />
        </label>
        <button className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700" onClick={handleCreate}>Create Project</button>
      </section>

      <section className="space-y-2 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Existing Projects</h2>
        <ul className="space-y-2">
          {projects?.map((p) => (
            <li key={p.id} className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-gray-900">
              <div className="flex items-center justify-between">
                <span>{p.name} ({p.id})</span>
                <button
                  className="rounded-lg border border-slate-300 px-3 py-1 text-sm hover:bg-slate-100 dark:border-gray-600 dark:hover:bg-gray-700"
                  onClick={() => triggerIngestion.mutate(p.id)}
                  disabled={triggerIngestion.isPending}
                >
                  Re-index
                </button>
              </div>
              {(p.repos.length > 0 || p.kbSources.length > 0) && (
                <ul className="mt-1 space-y-0.5 text-sm text-gray-500">
                  {p.repos.map((r) => (
                    <li key={`${r.owner}/${r.repo}`}>Repo: {r.owner}/{r.repo} ({r.defaultBranch})</li>
                  ))}
                  {p.kbSources.map((k) => (
                    <li key={k.location}>KB: {k.location}</li>
                  ))}
                </ul>
              )}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
