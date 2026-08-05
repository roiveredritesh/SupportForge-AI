// Shown while the pipeline (Triage -> FreshnessGate -> KB/Code/Vision research+verify) runs --
// QueryStream doesn't write anything to the response until the Drafter starts streaming tokens,
// so this is the only sign of life the UI can give during what's often the longest part of the wait.
export function ThinkingIndicator() {
  return (
    <div className="flex justify-start">
      <div className="flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-4 py-3 dark:border-gray-700 dark:bg-gray-800">
        <span className="flex gap-1">
          <span className="h-2 w-2 animate-bounce rounded-full bg-gray-400 [animation-delay:-0.3s]" />
          <span className="h-2 w-2 animate-bounce rounded-full bg-gray-400 [animation-delay:-0.15s]" />
          <span className="h-2 w-2 animate-bounce rounded-full bg-gray-400" />
        </span>
        <span className="text-sm text-gray-500 dark:text-gray-400">Thinking…</span>
      </div>
    </div>
  );
}
