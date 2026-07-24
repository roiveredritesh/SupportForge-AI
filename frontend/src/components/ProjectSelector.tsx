import { useProjects } from '../hooks/useProjects';

interface Props {
  value: string | null;
  onChange: (projectId: string) => void;
}

export function ProjectSelector({ value, onChange }: Props) {
  const { data: projects, isLoading } = useProjects();

  if (isLoading) return <div className="animate-pulse h-9 w-48 bg-gray-200 rounded" />;

  return (
    <select
      className="border rounded px-3 py-2"
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value)}
    >
      <option value="" disabled>Select a project</option>
      {projects?.map((p) => (
        <option key={p.id} value={p.id}>{p.name}</option>
      ))}
    </select>
  );
}
