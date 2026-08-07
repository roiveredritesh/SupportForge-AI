import ReactMarkdown from 'react-markdown';
import { ConfidenceBadge } from './ConfidenceBadge';
import type { ChatSource } from '../hooks/useChatQuery';

export interface MessageBubbleActions {
  onCopy: () => void;
  onMarkUseful: (useful: boolean) => void;
  onEscalate: () => void;
}

interface Props {
  role: 'user' | 'assistant';
  content: string;
  confidence?: number;
  sources?: ChatSource[];
  totalTokensUsed?: number;
  actions?: MessageBubbleActions;
}

// E1 (gap-closing-solutions.md Phase E): sources are rendered as separate UI chrome below the
// answer, never merged into `content` -- DrafterAgent's leak guard forbids citations *inside the
// drafted prose* (what "Copy Response" copies to send onward), not from this internal tool's own
// screen. This is for the support engineer's own verification, per the PRD's "Cited sources" screen.
export function MessageBubble({ role, content, confidence, sources, totalTokensUsed, actions }: Props) {
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
          <ReactMarkdown>{content}</ReactMarkdown>
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

        {actions && (
          <div className="flex gap-2 pt-2">
            <button className="rounded border px-3 py-1 text-sm" onClick={actions.onCopy}>Copy Response</button>
            <button className="rounded border px-3 py-1 text-sm" onClick={() => actions.onMarkUseful(true)}>Mark Useful</button>
            <button className="rounded border px-3 py-1 text-sm" onClick={() => actions.onMarkUseful(false)}>Not Useful</button>
            <button className="rounded border px-3 py-1 text-sm" onClick={actions.onEscalate}>Escalate</button>
          </div>
        )}
      </div>
    </div>
  );
}
