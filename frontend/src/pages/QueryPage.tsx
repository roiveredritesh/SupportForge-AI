import { useState } from 'react';
import { ScreenshotDropzone } from '../components/ScreenshotDropzone';
import { useChatQuery } from '../hooks/useChatQuery';
import { useAppStore } from '../store/useAppStore';
import { ResultsPanel } from '../components/ResultsPanel';
import { useSubmitFeedback } from '../hooks/useSubmitFeedback';

const STEPS = ['Triage', 'Research', 'Analysis', 'Drafting'] as const;

export default function QueryPage() {
  const { selectedProjectId } = useAppStore();
  const [query, setQuery] = useState('');
  const [screenshotBase64, setScreenshotBase64] = useState<string | undefined>();
  const chatQuery = useChatQuery();
  const submitFeedback = useSubmitFeedback();

  const handleSubmit = () => {
    if (!selectedProjectId) return;
    chatQuery.mutate({ projectId: selectedProjectId, query, screenshotBase64 });
  };

  return (
    <div className="mx-auto max-w-3xl space-y-4 p-6">
      <h1 className="text-xl font-semibold">Ask a question</h1>
      <textarea
        className="h-32 w-full rounded-lg border border-slate-300 p-3 focus:border-indigo-500 focus:outline-none dark:border-gray-600 dark:bg-gray-800"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Describe the issue..."
      />
      <ScreenshotDropzone onImageSelected={setScreenshotBase64} />
      <button
        className="rounded-lg bg-indigo-600 px-4 py-2 text-white hover:bg-indigo-700 disabled:opacity-50"
        onClick={handleSubmit}
        disabled={!selectedProjectId || !query || chatQuery.isPending}
      >
        Ask Agent
      </button>

      {chatQuery.isPending && (
        <p className="text-sm text-slate-500 dark:text-gray-400">{STEPS.join(' → ')}...</p>
      )}

      {chatQuery.data && (
        <ResultsPanel
          result={chatQuery.data}
          onCopy={() => navigator.clipboard.writeText(chatQuery.data!.draft)}
          onMarkUseful={(useful) => submitFeedback.mutate({ projectId: selectedProjectId!, query, useful, escalated: false })}
          onEscalate={() => submitFeedback.mutate({ projectId: selectedProjectId!, query, escalated: true })}
        />
      )}
      {chatQuery.isError && (
        <p className="text-sm text-red-600">
          Something went wrong. <button className="underline" onClick={handleSubmit}>Retry</button>
        </p>
      )}
    </div>
  );
}
