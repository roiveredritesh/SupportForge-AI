import { useState } from 'react';
import { useProjects, type Project, type ProjectKbSource, type ProjectRepo, type KbSourceType } from '../hooks/useProjects';
import { useCreateProject } from '../hooks/useCreateProject';
import { useTriggerIngestion } from '../hooks/useTriggerIngestion';
import { useForceReindex } from '../hooks/useForceReindex';
import { useDeleteProject } from '../hooks/useDeleteProject';
import { useFreshness } from '../hooks/useFreshness';
import { useDeadLetters, useDismissDeadLetter } from '../hooks/useDeadLetters';
import { useAppStore } from '../store/useAppStore';

function FreshnessBadge({ projectId }: { projectId: string }) {
  const { data } = useFreshness(projectId);
  if (!data) return null;
  const sourceLines = data.sources
    .map((s) => `${s.name}: ${s.lastSyncedAt ? new Date(s.lastSyncedAt).toLocaleString() : 'never synced'}`)
    .join('\n');
  return (
    <span
      className={
        data.isFresh
          ? 'rounded-full bg-green-100 px-2 py-0.5 text-xs text-green-700 dark:bg-green-900 dark:text-green-300'
          : 'rounded-full bg-amber-100 px-2 py-0.5 text-xs text-amber-700 dark:bg-amber-900 dark:text-amber-300'
      }
      title={sourceLines || 'No sources configured'}
    >
      {data.isFresh ? 'Fresh' : `Stale (${data.staleSources.length})`}
    </span>
  );
}

// C7 (gap-closing-solutions.md Phase C, item 7): permanently-failed ingestion jobs are otherwise
// only visible in server logs -- surfaces them here with a dismiss action, no auto-requeue (the
// existing "Re-index" button already re-runs the whole project).
function DeadLetterList({ projectId }: { projectId: string }) {
  const { data } = useDeadLetters(projectId);
  const dismiss = useDismissDeadLetter(projectId);
  if (!data || data.length === 0) return null;

  return (
    <li>
      <span className="font-medium text-red-600 dark:text-red-400">Failed ingestion jobs ({data.length}):</span>
      <ul className="ml-3 space-y-0.5">
        {data.map((entry) => (
          <li key={entry.id} className="flex items-center justify-between gap-2">
            <span title={entry.error}>
              {entry.jobType} — {new Date(entry.failedAt).toLocaleString()}
            </span>
            <button
              className="text-xs text-slate-500 hover:underline dark:text-gray-400"
              onClick={() => dismiss.mutate(entry.id)}
              disabled={dismiss.isPending}
            >
              Dismiss
            </button>
          </li>
        ))}
      </ul>
    </li>
  );
}

const emptyRepo: ProjectRepo = { owner: '', repo: '', defaultBranch: 'main', accessTokenSecretName: '' };
const emptyKbSource: ProjectKbSource = { type: 'Documents', location: '' };

const inputClass =
  'mt-1 w-full rounded-lg border border-slate-300 p-2 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900';

export default function AdminPage() {
  const { data: projects } = useProjects();
  const saveProject = useCreateProject();
  const triggerIngestion = useTriggerIngestion();
  const forceReindex = useForceReindex();
  const deleteProject = useDeleteProject();
  const { selectedProjectId, setSelectedProjectId } = useAppStore();

  const [editingId, setEditingId] = useState<string | null>(null);
  const [id, setId] = useState('');
  const [name, setName] = useState('');
  const [repos, setRepos] = useState<ProjectRepo[]>([]);
  const [kbSources, setKbSources] = useState<ProjectKbSource[]>([]);
  const [syncIntervalHours, setSyncIntervalHours] = useState('');

  const resetForm = () => {
    setEditingId(null);
    setId('');
    setName('');
    setRepos([]);
    setKbSources([]);
    setSyncIntervalHours('');
  };

  const startEdit = (p: Project) => {
    setEditingId(p.id);
    setId(p.id);
    setName(p.name);
    setRepos(p.repos.map((r) => ({ ...r })));
    setKbSources(p.kbSources.map((k) => ({ ...k })));
    setSyncIntervalHours(p.scheduledSyncIntervalHours != null ? String(p.scheduledSyncIntervalHours) : '');
    window.scrollTo({ top: 0, behavior: 'smooth' });
  };

  // Distinct from Re-index: also clears the project's content-hash cache first, so sources whose
  // *text* hasn't changed since the last sync still re-chunk under updated ingestion logic instead
  // of being skipped by KbVectorIndexer's unchanged-content short-circuit.
  const handleForceReindex = (projectId: string) => {
    if (!window.confirm('Force a full reindex? This clears the sync cache for every source on this project, so everything re-chunks and re-embeds even if unchanged. Use this after an ingestion logic update; a plain Re-index is enough otherwise.')) return;
    forceReindex.mutate(projectId);
  };

  const handleDelete = (projectId: string) => {
    if (!window.confirm('Delete this project and all its indexed data, feedback, and chat history? This cannot be undone.')) return;
    deleteProject.mutate(projectId);
    if (projectId === selectedProjectId) setSelectedProjectId(null);
    if (projectId === editingId) resetForm();
  };

  const handleSave = () => {
    const parsedInterval = syncIntervalHours.trim() === '' ? null : Number(syncIntervalHours);
    saveProject.mutate(
      {
        id,
        name,
        repos: repos.filter((r) => r.owner && r.repo),
        kbSources: kbSources.filter((k) => k.location),
        scheduledSyncIntervalHours: parsedInterval != null && !Number.isNaN(parsedInterval) ? parsedInterval : null,
      },
      { onSuccess: resetForm },
    );
  };

  const updateRepo = (index: number, patch: Partial<ProjectRepo>) => {
    setRepos((prev) => prev.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  };

  const updateKbSource = (index: number, patch: Partial<ProjectKbSource>) => {
    setKbSources((prev) => prev.map((k, i) => (i === index ? { ...k, ...patch } : k)));
  };

  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <h1 className="text-xl font-semibold">Project Administration</h1>

      <section className="space-y-4 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <div className="flex items-center justify-between">
          <h2 className="font-medium">{editingId ? `Edit Project (${editingId})` : 'Add Project'}</h2>
          {editingId && (
            <button className="text-sm text-slate-500 hover:underline dark:text-gray-400" onClick={resetForm}>
              Cancel edit
            </button>
          )}
        </div>

        <label className="block text-sm">
          Project ID
          <input className={inputClass} value={id} onChange={(e) => setId(e.target.value)} disabled={!!editingId} />
        </label>
        <label className="block text-sm">
          Project Name
          <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} />
        </label>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-sm font-medium">GitHub Repos</span>
            <button
              className="text-sm text-indigo-600 hover:underline dark:text-indigo-400"
              onClick={() => setRepos((prev) => [...prev, { ...emptyRepo }])}
            >
              + Add repo
            </button>
          </div>
          {repos.map((r, i) => (
            <div key={i} className="space-y-2 rounded-lg bg-slate-50 p-3 dark:bg-gray-900">
              <div className="grid grid-cols-2 gap-2">
                <input className={inputClass} placeholder="Owner" value={r.owner} onChange={(e) => updateRepo(i, { owner: e.target.value })} />
                <input className={inputClass} placeholder="Repo" value={r.repo} onChange={(e) => updateRepo(i, { repo: e.target.value })} />
              </div>
              <div className="grid grid-cols-2 gap-2">
                <input
                  className={inputClass}
                  placeholder="Default branch"
                  value={r.defaultBranch}
                  onChange={(e) => updateRepo(i, { defaultBranch: e.target.value })}
                />
                <input
                  className={inputClass}
                  placeholder="Access token secret name (optional, private repos)"
                  value={r.accessTokenSecretName ?? ''}
                  onChange={(e) => updateRepo(i, { accessTokenSecretName: e.target.value })}
                />
              </div>
              <button
                className="text-sm text-red-600 hover:underline dark:text-red-400"
                onClick={() => setRepos((prev) => prev.filter((_, idx) => idx !== i))}
              >
                Remove
              </button>
            </div>
          ))}
        </div>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-sm font-medium">Knowledge Base Sources</span>
            <button
              className="text-sm text-indigo-600 hover:underline dark:text-indigo-400"
              onClick={() => setKbSources((prev) => [...prev, { ...emptyKbSource }])}
            >
              + Add source
            </button>
          </div>
          {kbSources.map((k, i) => (
            <div key={i} className="space-y-2 rounded-lg bg-slate-50 p-3 dark:bg-gray-900">
              <select
                className={inputClass}
                value={k.type}
                onChange={(e) => updateKbSource(i, { type: e.target.value as KbSourceType })}
              >
                <option value="Documents">Documents</option>
                <option value="Confluence">Confluence</option>
                <option value="Website">Website</option>
              </select>
              <input
                className={inputClass}
                placeholder={
                  k.type === 'Documents'
                    ? 'Folder path, or a github.com/owner/repo(/tree/branch/path) URL'
                    : k.type === 'Confluence'
                      ? 'Confluence page ID'
                      : 'Website URL'
                }
                value={k.location}
                onChange={(e) => updateKbSource(i, { location: e.target.value })}
              />
              {k.type === 'Documents' && (
                <div className="grid grid-cols-2 gap-2">
                  <select
                    className={inputClass}
                    value={k.repoOwner && k.repoName ? `${k.repoOwner}/${k.repoName}` : ''}
                    onChange={(e) => {
                      const [owner, repo] = e.target.value.split('/');
                      updateKbSource(i, { repoOwner: owner ?? null, repoName: repo ?? null });
                    }}
                  >
                    <option value="">Standalone path (no repo)</option>
                    {repos.filter((r) => r.owner && r.repo).map((r) => (
                      <option key={`${r.owner}/${r.repo}`} value={`${r.owner}/${r.repo}`}>
                        {r.owner}/{r.repo}
                      </option>
                    ))}
                  </select>
                </div>
              )}
              {k.type === 'Website' && (
                <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-gray-300">
                  <input
                    type="checkbox"
                    checked={k.crawlLinkedPages ?? false}
                    onChange={(e) => updateKbSource(i, { crawlLinkedPages: e.target.checked })}
                  />
                  Also index pages linked from this page (same site only)
                </label>
              )}
              <button
                className="text-sm text-red-600 hover:underline dark:text-red-400"
                onClick={() => setKbSources((prev) => prev.filter((_, idx) => idx !== i))}
              >
                Remove
              </button>
            </div>
          ))}
        </div>

        <label className="block text-sm">
          Scheduled KB sync interval (hours, optional)
          <input
            className={inputClass}
            type="number"
            min="1"
            placeholder="Default (server-wide setting)"
            value={syncIntervalHours}
            onChange={(e) => setSyncIntervalHours(e.target.value)}
          />
        </label>

        <button className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700" onClick={handleSave} disabled={saveProject.isPending}>
          {editingId ? 'Save Changes' : 'Create Project'}
        </button>
      </section>

      <section className="space-y-2 rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
        <h2 className="font-medium">Existing Projects</h2>
        <ul className="space-y-2">
          {projects?.map((p) => (
            <li key={p.id} className="rounded-lg bg-slate-50 px-3 py-2 dark:bg-gray-900">
              <div className="flex items-center justify-between">
                <span className="flex items-center gap-2">
                  {p.name} ({p.id})
                  <FreshnessBadge projectId={p.id} />
                </span>
                <div className="flex gap-2">
                  <button
                    className="rounded-lg border border-slate-300 px-3 py-1 text-sm hover:bg-slate-100 dark:border-gray-600 dark:hover:bg-gray-700"
                    onClick={() => startEdit(p)}
                  >
                    Edit
                  </button>
                  <button
                    className="rounded-lg border border-slate-300 px-3 py-1 text-sm hover:bg-slate-100 dark:border-gray-600 dark:hover:bg-gray-700"
                    onClick={() => triggerIngestion.mutate(p.id)}
                    disabled={triggerIngestion.isPending}
                  >
                    Re-index
                  </button>
                  <button
                    className="rounded-lg border border-slate-300 px-3 py-1 text-sm hover:bg-slate-100 dark:border-gray-600 dark:hover:bg-gray-700"
                    onClick={() => handleForceReindex(p.id)}
                    disabled={forceReindex.isPending}
                    title="Clears the sync cache first, so unchanged sources re-chunk too -- use after an ingestion logic update"
                  >
                    Force Reindex
                  </button>
                  <button
                    className="rounded-lg border border-red-300 px-3 py-1 text-sm text-red-600 hover:bg-red-50 dark:border-red-700 dark:hover:bg-red-950"
                    onClick={() => handleDelete(p.id)}
                    disabled={deleteProject.isPending}
                  >
                    Delete
                  </button>
                </div>
              </div>
              {(p.repos.length > 0 || p.kbSources.length > 0) && (
                <ul className="mt-1 space-y-0.5 text-sm text-gray-500">
                  {p.repos.map((r) => (
                    <li key={`${r.owner}/${r.repo}`}>
                      Repo: {r.owner}/{r.repo} ({r.defaultBranch})
                      {r.lastSyncedAt ? ` — last synced ${new Date(r.lastSyncedAt).toLocaleString()}` : ' — never synced'}
                    </li>
                  ))}
                  {p.kbSources.map((k) => (
                    <li key={`${k.type}:${k.location}`}>
                      KB [{k.type}]: {k.location}
                      {k.repoOwner && k.repoName ? ` (via ${k.repoOwner}/${k.repoName})` : ''}
                      {k.type === 'Website' && k.crawlLinkedPages ? ' (+ linked pages)' : ''}
                      {k.lastSyncedAt ? ` — last synced ${new Date(k.lastSyncedAt).toLocaleString()}` : ' — never synced'}
                    </li>
                  ))}
                  {p.kbSources.length > 0 && (
                    <li>
                      Scheduled sync: every {p.scheduledSyncIntervalHours ?? '(default)'}
                      {p.scheduledSyncIntervalHours != null ? 'h' : ''}
                    </li>
                  )}
                </ul>
              )}
              <ul className="mt-1 space-y-0.5 text-sm">
                <DeadLetterList projectId={p.id} />
              </ul>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
