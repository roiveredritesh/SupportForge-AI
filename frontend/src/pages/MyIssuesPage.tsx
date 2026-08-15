import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useMyIssues, type Escalation } from '../hooks/useEscalations';

type StatusFilter = 'All' | 'Claimed' | 'Resolved';

// U21: the calling engineer's claimed-open + recently-resolved escalations, with a status filter.
export default function MyIssuesPage() {
  const { data: issues, isLoading } = useMyIssues();
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('All');
  const [openId, setOpenId] = useState<string | null>(null);

  const filtered = (issues ?? []).filter((e) => statusFilter === 'All' || e.status === statusFilter);
  const open = filtered.find((e) => e.id === openId);

  return (
    <div className="mx-auto max-w-3xl space-y-4 p-6">
      <h1 className="text-xl font-semibold">My Issues</h1>

      <div className="flex gap-2 text-sm">
        {(['All', 'Claimed', 'Resolved'] as StatusFilter[]).map((s) => (
          <button
            key={s}
            className={`rounded-full px-3 py-1 ${statusFilter === s ? 'bg-indigo-600 text-white' : 'border border-slate-300 dark:border-gray-600'}`}
            onClick={() => setStatusFilter(s)}
          >
            {s}
          </button>
        ))}
      </div>

      {isLoading && <p className="text-sm text-gray-500">Loading...</p>}
      {filtered.length === 0 && !isLoading && <p className="text-sm text-gray-500">No issues here.</p>}

      <ul className="space-y-2">
        {filtered.map((e: Escalation) => (
          <li key={e.id} className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800">
            <button className="text-left hover:underline" onClick={() => setOpenId(openId === e.id ? null : e.id)}>
              Conversation {e.conversationId} — {e.status}
              {e.claimedAt ? ` — claimed ${new Date(e.claimedAt).toLocaleString()}` : ''}
            </button>
          </li>
        ))}
      </ul>

      {open && (
        <div className="rounded-xl border border-slate-200 bg-white p-5 dark:border-gray-700 dark:bg-gray-800">
          <div className="prose prose-sm max-w-none dark:prose-invert">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>{open.markdown}</ReactMarkdown>
          </div>
        </div>
      )}
    </div>
  );
}
