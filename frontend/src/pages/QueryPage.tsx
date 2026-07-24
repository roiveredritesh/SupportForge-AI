import { useState } from 'react';
import { ScreenshotDropzone } from '../components/ScreenshotDropzone';
import { useChatQuery } from '../hooks/useChatQuery';
import { useAppStore } from '../store/useAppStore';
import { ResultsPanel } from '../components/ResultsPanel';

const STEPS = ['Triage', 'Research', 'Analysis', 'Drafting'] as const;

export default function QueryPage() {
  const { selectedProjectId } = useAppStore();
  const [query, setQuery] = useState('');
  const [screenshotBase64, setScreenshotBase64] = useState<string | undefined>();
  const chatQuery = useChatQuery();

  const handleSubmit = () => {
    if (!selectedProjectId) return;
    chatQuery.mutate({ projectId: selectedProjectId, query, screenshotBase64 });
  };

  return (
    <div className="p-6 max-w-3xl mx-auto space-y-4">
      <h1 className="text-xl font-semibold">Ask a question</h1>
      <textarea
        className="w-full border rounded p-3 h-32"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Describe the issue..."
      />
      <ScreenshotDropzone onImageSelected={setScreenshotBase64} />
      <button
        className="bg-blue-600 text-white px-4 py-2 rounded disabled:opacity-50"
        onClick={handleSubmit}
        disabled={!selectedProjectId || !query || chatQuery.isPending}
      >
        Ask Agent
      </button>

      {chatQuery.isPending && (
        <p className="text-sm text-gray-500">{STEPS.join(' → ')}...</p>
      )}

      {chatQuery.data && <ResultsPanel result={chatQuery.data} />}
      {chatQuery.isError && (
        <p className="text-sm text-red-600">
          Something went wrong. <button className="underline" onClick={handleSubmit}>Retry</button>
        </p>
      )}
    </div>
  );
}
