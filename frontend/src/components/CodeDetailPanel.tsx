import { useState } from 'react';
import type { BlastRadiusEntry, CommitInfo } from '../hooks/useChatQuery';

interface Props {
  codeDetails?: string[];
  commitHistory?: CommitInfo[];
  blastRadius?: BlastRadiusEntry[];
}

// U9: renders CodeAnalyzerAgent's raw findings (paths/line ranges/snippets) when the backend
// included them -- only L2/L3/Admin responses ever carry this field (ChatController's role gate,
// U6), L1 responses omit it entirely, so "nothing to render" is the correct default here, not an
// error state. Collapsible and separate from MessageBubble's answer text: this is the internal
// engineer's own drill-down, never merged into the customer-facing draft.
//
// U11: commitHistory carries the same L2/L3/Admin-only gate -- recent commits (author, date,
// message, PR link) for the files codeDetails matched, rendered under the code details themselves.
//
// U24: blastRadius carries the same L2/L3/Admin-only gate -- other repos in the project that call
// an endpoint one of codeDetails' files defines, rendered as a structured list ("Repo A -> used by
// Repo B, Repo C"), not a graph visualization (PRD 5.4 explicitly allows this simpler surface).
export function CodeDetailPanel({ codeDetails, commitHistory, blastRadius }: Props) {
  const [expanded, setExpanded] = useState(false);
  const hasCodeDetails = !!codeDetails && codeDetails.length > 0;
  const hasCommits = !!commitHistory && commitHistory.length > 0;
  const hasBlastRadius = !!blastRadius && blastRadius.length > 0;
  if (!hasCodeDetails && !hasCommits && !hasBlastRadius) return null;

  return (
    <div className="border-t border-slate-100 pt-2 text-xs dark:border-gray-700">
      <button
        className="font-medium text-indigo-600 hover:underline dark:text-indigo-400"
        onClick={() => setExpanded((e) => !e)}
      >
        {expanded ? 'Hide' : 'Show'} code details ({(codeDetails?.length ?? 0) + (commitHistory?.length ?? 0) + (blastRadius?.length ?? 0)})
      </button>
      {expanded && (
        <>
          {hasCodeDetails && (
            <ul className="mt-1 space-y-1">
              {codeDetails.map((detail, i) => (
                <li key={i} className="whitespace-pre-wrap rounded bg-slate-50 p-2 font-mono text-[11px] dark:bg-gray-900">
                  {detail}
                </li>
              ))}
            </ul>
          )}
          {hasCommits && (
            <div className="mt-2">
              <div className="font-medium text-gray-500 dark:text-gray-400">Recent commits</div>
              <ul className="mt-1 space-y-1">
                {commitHistory.map((commit, i) => (
                  <li key={commit.sha ?? i} className="rounded bg-slate-50 p-2 dark:bg-gray-900">
                    <div>
                      <span className="font-mono">{commit.sha.slice(0, 7)}</span> {commit.message}
                    </div>
                    <div className="mt-0.5 text-gray-500 dark:text-gray-400">
                      {commit.author} &middot; {new Date(commit.date).toLocaleDateString()}
                      {commit.prUrl && (
                        <>
                          {' '}
                          &middot;{' '}
                          <a
                            className="text-indigo-600 hover:underline dark:text-indigo-400"
                            href={commit.prUrl}
                            target="_blank"
                            rel="noreferrer"
                          >
                            PR
                          </a>
                        </>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          )}
          {hasBlastRadius && (
            <div className="mt-2">
              <div className="font-medium text-gray-500 dark:text-gray-400">Blast radius</div>
              <ul className="mt-1 space-y-1">
                {blastRadius.map((entry) => (
                  <li key={entry.repo} className="rounded bg-slate-50 p-2 dark:bg-gray-900">
                    <span className="font-mono">{entry.repo}</span> &rarr; used by{' '}
                    <span className="font-mono">{entry.usedBy.join(', ')}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </>
      )}
    </div>
  );
}
