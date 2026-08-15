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
    <div className="mx-auto max-w-3xl space-y-4 p-6">
      <h1 className="text-xl font-semibold">Escalation Queue</h1>

      {isLoading && <p className="text-sm text-gray-500">Loading...</p>}
      {escalations?.length === 0 && <p className="text-sm text-gray-500">No open escalations.</p>}

      <ul className="space-y-2">
        {escalations?.map((e) => (
          <EscalationRow key={e.id} escalation={e} isOpen={openId === e.id} onToggle={() => setOpenId(openId === e.id ? null : e.id)} onClaim={() => claim.mutate(e.id)} claimPending={claim.isPending} />
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

function EscalationRow({
  escalation, isOpen, onToggle, onClaim, claimPending,
}: {
  escalation: Escalation; isOpen: boolean; onToggle: () => void; onClaim: () => void; claimPending: boolean;
}) {
  return (
    <li
      className={`flex items-center justify-between rounded-lg border px-3 py-2 text-sm dark:bg-gray-800 ${
        isOpen ? 'border-indigo-400 bg-indigo-50 dark:border-indigo-500' : 'border-slate-200 bg-white dark:border-gray-700'
      }`}
    >
      <button className="text-left hover:underline" onClick={onToggle}>
        Conversation {escalation.conversationId} — escalated {new Date(escalation.escalatedAt).toLocaleString()}
      </button>
      <button className="rounded border px-3 py-1 text-sm" onClick={onClaim} disabled={claimPending}>
        Claim
      </button>
    </li>
  );
}
