import ReactMarkdown from 'react-markdown';
import { ConfidenceBadge } from './ConfidenceBadge';

export interface MessageBubbleActions {
  onCopy: () => void;
  onMarkUseful: (useful: boolean) => void;
  onEscalate: () => void;
}

interface Props {
  role: 'user' | 'assistant';
  content: string;
  confidence?: number;
  actions?: MessageBubbleActions;
}

export function MessageBubble({ role, content, confidence, actions }: Props) {
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
