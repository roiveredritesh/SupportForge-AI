import { useState } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { ConfidenceBadge } from './ConfidenceBadge';
import { CodeDetailPanel } from './CodeDetailPanel';
import type { BlastRadiusEntry, ChatSource, CommitInfo } from '../hooks/useChatQuery';
import type { FeedbackReasonCode } from '../hooks/useSubmitFeedback';

const REASON_CODES: { value: FeedbackReasonCode; label: string }[] = [
  { value: 'Irrelevant', label: 'Irrelevant' },
  { value: 'WrongVersion', label: 'Wrong version' },
  { value: 'Incomplete', label: 'Incomplete' },
  { value: 'Other', label: 'Other' },
];

export interface MessageBubbleActions {
  onCopy: () => void;
  // U17: reasonCode is required by the backend when useful=false -- MessageBubble collects it via
  // the inline reason-code select before calling this.
  onMarkUseful: (useful: boolean, reasonCode?: FeedbackReasonCode) => void;
  onEscalate: () => void | Promise<unknown>;
}

interface Props {
  role: 'user' | 'assistant';
  content: string;
  confidence?: number;
  sources?: ChatSource[];
  totalTokensUsed?: number;
  codeDetails?: string[];
  commitHistory?: CommitInfo[];
  blastRadius?: BlastRadiusEntry[];
  actions?: MessageBubbleActions;
}

// E1 (gap-closing-solutions.md Phase E): sources are rendered as separate UI chrome below the
// answer, never merged into `content` -- DrafterAgent's leak guard forbids citations *inside the
// drafted prose* (what "Copy Response" copies to send onward), not from this internal tool's own
// screen. This is for the support engineer's own verification, per the PRD's "Cited sources" screen.
export function MessageBubble({ role, content, confidence, sources, totalTokensUsed, codeDetails, commitHistory, blastRadius, actions }: Props) {
  // U17/U20: "Not Useful" reveals a reason-code select instead of submitting immediately -- the
  // backend rejects useful=false without one. "Mark Useful" still submits straight away.
  const [pickingReason, setPickingReason] = useState(false);
  const [reasonCode, setReasonCode] = useState<FeedbackReasonCode | ''>('');
  const [escalated, setEscalated] = useState(false);

  const submitNotUseful = () => {
    if (!reasonCode) return;
    actions?.onMarkUseful(false, reasonCode);
    setPickingReason(false);
    setReasonCode('');
  };

  const handleEscalate = async () => {
    if (!actions) return;
    await actions.onEscalate();
    setEscalated(true);
  };

  if (role === 'user') {
    return (
      <div className="flex justify-end">
        <div className="max-w-[80%] whitespace-pre-wrap rounded-lg bg-indigo-600 px-4 py-2 text-white">
          {content}
        </div>
      </div>
    );
  }

  return (
    <div className="flex justify-start">
      <div className="max-w-[80%] space-y-3 rounded-lg border border-slate-200 bg-white p-4 dark:border-gray-700 dark:bg-gray-800">
        {confidence !== undefined && <ConfidenceBadge confidence={confidence} />}

        <div className="prose prose-sm max-w-none dark:prose-invert">
          <ReactMarkdown remarkPlugins={[remarkGfm]}>{content}</ReactMarkdown>
        </div>

        {((sources && sources.length > 0) || totalTokensUsed !== undefined) && (
          <div className="border-t border-slate-100 pt-2 text-xs text-gray-500 dark:border-gray-700">
            {sources && sources.length > 0 && (
              <div>
                <span className="font-medium">Sources:</span>{' '}
                {[...new Set(sources.map((s) => s.label))].join(', ')}
              </div>
            )}
            {totalTokensUsed !== undefined && (
              <div className={sources && sources.length > 0 ? 'mt-1' : undefined}>
                <span className="font-medium">Tokens used:</span> {totalTokensUsed.toLocaleString()}
              </div>
            )}
          </div>
        )}

        <CodeDetailPanel codeDetails={codeDetails} commitHistory={commitHistory} blastRadius={blastRadius} />

        {actions && (
          <div className="space-y-2 pt-2">
            <div className="flex flex-wrap gap-2">
              <button className="rounded border px-3 py-1 text-sm" onClick={actions.onCopy}>Copy Response</button>
              <button className="rounded border px-3 py-1 text-sm" onClick={() => actions.onMarkUseful(true)}>Mark Useful</button>
              <button className="rounded border px-3 py-1 text-sm" onClick={() => setPickingReason(true)}>Not Useful</button>
              <button className="rounded border px-3 py-1 text-sm" onClick={handleEscalate} disabled={escalated}>
                {escalated ? 'Escalated' : 'Escalate'}
              </button>
            </div>

            {pickingReason && (
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <label htmlFor="feedback-reason-code">Reason:</label>
                <select
                  id="feedback-reason-code"
                  className="rounded border px-2 py-1 text-sm dark:bg-gray-900"
                  value={reasonCode}
                  onChange={(e) => setReasonCode(e.target.value as FeedbackReasonCode)}
                >
                  <option value="">Select a reason...</option>
                  {REASON_CODES.map((r) => (
                    <option key={r.value} value={r.value}>{r.label}</option>
                  ))}
                </select>
                <button
                  className="rounded border px-3 py-1 text-sm disabled:opacity-50"
                  onClick={submitNotUseful}
                  disabled={!reasonCode}
                >
                  Submit
                </button>
                <button className="text-sm text-gray-500 hover:underline" onClick={() => setPickingReason(false)}>
                  Cancel
                </button>
              </div>
            )}

            {escalated && <p className="text-xs text-green-600 dark:text-green-400">Escalated to the support queue.</p>}
          </div>
        )}
      </div>
    </div>
  );
}
