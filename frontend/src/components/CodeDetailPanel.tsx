import { useState } from 'react';

interface Props {
  codeDetails?: string[];
}

// U9: renders CodeAnalyzerAgent's raw findings (paths/line ranges/snippets) when the backend
// included them -- only L2/L3/Admin responses ever carry this field (ChatController's role gate,
// U6), L1 responses omit it entirely, so "nothing to render" is the correct default here, not an
// error state. Collapsible and separate from MessageBubble's answer text: this is the internal
// engineer's own drill-down, never merged into the customer-facing draft.
export function CodeDetailPanel({ codeDetails }: Props) {
  const [expanded, setExpanded] = useState(false);
  if (!codeDetails || codeDetails.length === 0) return null;

  return (
    <div className="border-t border-slate-100 pt-2 text-xs dark:border-gray-700">
      <button
        className="font-medium text-indigo-600 hover:underline dark:text-indigo-400"
        onClick={() => setExpanded((e) => !e)}
      >
        {expanded ? 'Hide' : 'Show'} code details ({codeDetails.length})
      </button>
      {expanded && (
        <ul className="mt-1 space-y-1">
          {codeDetails.map((detail, i) => (
            <li key={i} className="whitespace-pre-wrap rounded bg-slate-50 p-2 font-mono text-[11px] dark:bg-gray-900">
              {detail}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
