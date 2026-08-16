import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useEscalationQueue, useClaimEscalation, type Escalation } from '../hooks/useEscalations';

// U21: L2/L3/Admin queue of open escalations -- opening one shows the cached Markdown (full code
// detail/blast radius already baked in at escalation time, see EscalationsController.Escalate)
// directly, no re-query. Server-side role gate is GET /api/escalations' [Authorize(Roles=...)];
// this page renders for anyone the router lets reach it, same convention as AdminPage's sections.
export default function EscalationQueuePage() {
  const { data: escalations, isLoading } = useEscalationQueue();
  const claim = useClaimEscalation();
  const [openId, setOpenId] = useState<string | null>(null);

  const open = escalations?.find((e) => e.id === openId);

  return (
    <div className="mx-auto max-w-4xl space-y-4 p-6">
      <h1 className="text-xl font-semibold">Escalation Queue</h1>

      {isLoading && <p className="text-sm text-gray-500">Loading...</p>}
      {escalations?.length === 0 && <p className="text-sm text-gray-500">No open escalations.</p>}

      {escalations && escalations.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-gray-700">
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase text-slate-400 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-500">
                <th className="py-2 px-3">Conversation</th>
                <th className="py-2 px-3">Escalated</th>
                <th className="py-2 px-3"></th>
              </tr>
            </thead>
            <tbody>
              {escalations.map((e) => (
                <EscalationRow
                  key={e.id}
                  escalation={e}
                  isOpen={openId === e.id}
                  onToggle={() => setOpenId(openId === e.id ? null : e.id)}
                  onClaim={() => claim.mutate(e.id)}
                  claimPending={claim.isPending}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}

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

function EscalationRow({
  escalation, isOpen, onToggle, onClaim, claimPending,
}: {
  escalation: Escalation; isOpen: boolean; onToggle: () => void; onClaim: () => void; claimPending: boolean;
}) {
  return (
    <tr className={`border-b border-slate-100 dark:border-gray-800 ${isOpen ? 'bg-indigo-50 dark:bg-indigo-950' : 'bg-white dark:bg-gray-800'}`}>
      <td className="py-2 px-3">
        <button className="text-left hover:underline" onClick={onToggle}>
          {escalation.conversationId}
        </button>
      </td>
      <td className="py-2 px-3">{new Date(escalation.escalatedAt).toLocaleString()}</td>
      <td className="py-2 px-3 text-right">
        <button className="rounded border px-3 py-1 text-sm" onClick={onClaim} disabled={claimPending}>
          Claim
        </button>
      </td>
    </tr>
  );
}
