import { useProjects } from '../hooks/useProjects';
import { useAppStore } from '../store/useAppStore';

interface Props {
  hasMessages: boolean;
  onSwitch: () => void;
}

export function ProjectSwitcher({ hasMessages, onSwitch }: Props) {
  const { data: projects } = useProjects();
  const { selectedProjectId, setSelectedProjectId } = useAppStore();

  const handleChange = (id: string) => {
    if (!id || id === selectedProjectId) return;
    if (hasMessages && !window.confirm('Switching projects will start a new chat. Continue?')) return;
    setSelectedProjectId(id);
    onSwitch();
  };

  return (
    <select
      className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-900"
      value={selectedProjectId ?? ''}
      onChange={(e) => handleChange(e.target.value)}
    >
      <option value="" disabled>Select a project</option>
      {projects?.map((p) => (
        <option key={p.id} value={p.id}>{p.name}</option>
      ))}
    </select>
  );
}
