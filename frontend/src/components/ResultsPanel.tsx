import type { ChatQueryResponse } from '../hooks/useChatQuery';
import ReactMarkdown from 'react-markdown';
import { ConfidenceBadge } from './ConfidenceBadge';

interface Props {
  result: ChatQueryResponse;
  onCopy?: () => void;
  onMarkUseful?: (useful: boolean) => void;
  onEscalate?: () => void;
}

export function ResultsPanel({ result, onCopy, onMarkUseful, onEscalate }: Props) {
  return (
    <div className="border rounded p-4 space-y-3">
      <div className="flex justify-between items-center">
        <ConfidenceBadge confidence={result.confidence} />
      </div>

      <div className="prose prose-sm max-w-none">
        <ReactMarkdown>{result.draft}</ReactMarkdown>
      </div>

      {result.sources.length > 0 && (
        <div className="text-sm text-gray-600">
          <strong>Sources:</strong>
          <ul className="list-disc list-inside">
            {result.sources.map((s) => (
              <li key={s.url}>{s.label}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="flex gap-2 pt-2">
        <button className="text-sm border rounded px-3 py-1" onClick={onCopy}>Copy Response</button>
        <button className="text-sm border rounded px-3 py-1" onClick={() => onMarkUseful?.(true)}>Mark Useful</button>
        <button className="text-sm border rounded px-3 py-1" onClick={() => onMarkUseful?.(false)}>Not Useful</button>
        <button className="text-sm border rounded px-3 py-1" onClick={onEscalate}>Escalate</button>
      </div>
    </div>
  );
}
