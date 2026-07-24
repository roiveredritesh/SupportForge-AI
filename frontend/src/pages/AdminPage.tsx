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
    <div className="p-6 max-w-2xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">Project Administration</h1>

      <section className="space-y-2 border rounded p-4">
        <h2 className="font-medium">Add Project</h2>
        <label className="block text-sm">Project ID
          <input className="border rounded w-full p-2" value={id} onChange={(e) => setId(e.target.value)} />
        </label>
        <label className="block text-sm">Project Name
          <input className="border rounded w-full p-2" value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="block text-sm">GitHub Repo Owner
          <input className="border rounded w-full p-2" value={repoOwner} onChange={(e) => setRepoOwner(e.target.value)} />
        </label>
        <label className="block text-sm">GitHub Repo Name
          <input className="border rounded w-full p-2" value={repoName} onChange={(e) => setRepoName(e.target.value)} />
        </label>
        <label className="block text-sm">KB Documents Folder
          <input className="border rounded w-full p-2" value={kbFolder} onChange={(e) => setKbFolder(e.target.value)} />
        </label>
        <button className="bg-blue-600 text-white px-4 py-2 rounded" onClick={handleCreate}>Create Project</button>
      </section>

      <section className="space-y-2 border rounded p-4">
        <h2 className="font-medium">Existing Projects</h2>
        <ul className="space-y-2">
          {projects?.map((p) => (
            <li key={p.id} className="flex justify-between items-center">
              <span>{p.name} ({p.id})</span>
              <button
                className="text-sm border rounded px-3 py-1"
                onClick={() => triggerIngestion.mutate(p.id)}
                disabled={triggerIngestion.isPending}
              >
                Re-index
              </button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
