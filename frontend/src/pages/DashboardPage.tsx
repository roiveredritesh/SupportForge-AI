import { Link } from 'react-router-dom';
import {
  Line,
  LineChart,
  Pie,
  PieChart,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
  Legend,
} from 'recharts';
import { ProjectSelector } from '../components/ProjectSelector';
import { useAppStore } from '../store/useAppStore';
import { useQueryVolume } from '../hooks/useQueryVolume';
import { useFeedbackSummary } from '../hooks/useFeedbackSummary';
import { useEscalationStats } from '../hooks/useEscalationStats';
import { useTokenUsage } from '../hooks/useTokenUsage';

const FEEDBACK_COLORS = ['#4f46e5', '#f59e0b'];

function ChartCard({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
      <h2 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-gray-500">
        {title}
      </h2>
      {children}
    </section>
  );
}

function EmptyState({ label }: { label: string }) {
  return <p className="py-8 text-center text-sm text-slate-400 dark:text-gray-500">{label}</p>;
}

function QueryVolumeChart({ projectId }: { projectId: string }) {
  const { data } = useQueryVolume(projectId);
  const points = data ?? [];

  return (
    <ChartCard title="Query Volume (last 30 days)">
      {points.length === 0 ? (
        <EmptyState label="No queries recorded yet." />
      ) : (
        <ResponsiveContainer width="100%" height={220}>
          <LineChart data={points}>
            <XAxis dataKey="date" tick={{ fontSize: 11 }} />
            <YAxis allowDecimals={false} tick={{ fontSize: 11 }} />
            <Tooltip />
            <Line type="monotone" dataKey="count" stroke="#4f46e5" strokeWidth={2} dot={false} />
          </LineChart>
        </ResponsiveContainer>
      )}
    </ChartCard>
  );
}

function FeedbackRatioChart({ projectId }: { projectId: string }) {
  const { data } = useFeedbackSummary(projectId);
  const useful = data?.useful ?? 0;
  const notUseful = data?.notUseful ?? 0;
  const slices = [
    { name: 'Useful', value: useful },
    { name: 'Not useful', value: notUseful },
  ];

  return (
    <ChartCard title="Feedback Ratio">
      {useful + notUseful === 0 ? (
        <EmptyState label="No feedback recorded yet." />
      ) : (
        <ResponsiveContainer width="100%" height={220}>
          <PieChart>
            <Pie data={slices} dataKey="value" nameKey="name" innerRadius={50} outerRadius={80}>
              {slices.map((s, i) => (
                <Cell key={s.name} fill={FEEDBACK_COLORS[i]} />
              ))}
            </Pie>
            <Legend />
            <Tooltip />
          </PieChart>
        </ResponsiveContainer>
      )}
    </ChartCard>
  );
}

function EscalationStatsRow({ projectId }: { projectId: string }) {
  const { data } = useEscalationStats(projectId);
  const stats = [
    { label: 'Open', value: data?.open ?? 0, color: 'text-amber-600 dark:text-amber-400' },
    { label: 'Claimed', value: data?.claimed ?? 0, color: 'text-indigo-600 dark:text-indigo-400' },
    { label: 'Resolved', value: data?.resolved ?? 0, color: 'text-green-600 dark:text-green-400' },
  ];

  return (
    <ChartCard title="Escalations">
      <div className="grid grid-cols-3 gap-3 text-center">
        {stats.map((s) => (
          <div key={s.label} className="rounded-lg bg-slate-50 py-4 dark:bg-gray-900">
            <div className={`text-2xl font-semibold ${s.color}`}>{s.value}</div>
            <div className="text-xs text-slate-500 dark:text-gray-400">{s.label}</div>
          </div>
        ))}
      </div>
    </ChartCard>
  );
}

function TokenUsageBreakdown({ projectId }: { projectId: string }) {
  const { data } = useTokenUsage(projectId);
  const chat = data?.bySource?.chat ?? 0;
  const ingestion = data?.bySource?.ingestion ?? 0;
  const total = chat + ingestion;

  return (
    <ChartCard title="Token Usage">
      {total === 0 ? (
        <EmptyState label="No token usage recorded yet." />
      ) : (
        <div className="space-y-3">
          <div className="flex h-4 overflow-hidden rounded-full bg-slate-100 dark:bg-gray-900">
            <div className="bg-indigo-600" style={{ width: `${(chat / total) * 100}%` }} title={`Chat: ${chat}`} />
            <div className="bg-amber-500" style={{ width: `${(ingestion / total) * 100}%` }} title={`Ingestion: ${ingestion}`} />
          </div>
          <div className="flex justify-between text-sm">
            <span className="text-slate-600 dark:text-gray-300">
              <span className="mr-1 inline-block h-2 w-2 rounded-full bg-indigo-600" /> Chat: {chat.toLocaleString()}
            </span>
            <span className="text-slate-600 dark:text-gray-300">
              <span className="mr-1 inline-block h-2 w-2 rounded-full bg-amber-500" /> Ingestion: {ingestion.toLocaleString()}
            </span>
          </div>
        </div>
      )}
    </ChartCard>
  );
}

export default function DashboardPage() {
  const { selectedProjectId, setSelectedProjectId } = useAppStore();

  return (
    <div className="mx-auto max-w-4xl space-y-6 p-6">
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

      {selectedProjectId && (
        <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
          <QueryVolumeChart projectId={selectedProjectId} />
          <FeedbackRatioChart projectId={selectedProjectId} />
          <EscalationStatsRow projectId={selectedProjectId} />
          <TokenUsageBreakdown projectId={selectedProjectId} />
        </div>
      )}
    </div>
  );
}
