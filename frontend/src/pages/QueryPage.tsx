import { useState } from 'react';
import { Link } from 'react-router-dom';
import { ScreenshotDropzone } from '../components/ScreenshotDropzone';
import { useChatQueryStream } from '../hooks/useChatQueryStream';
import { useAppStore } from '../store/useAppStore';
import { ResultsPanel } from '../components/ResultsPanel';
import { useSubmitFeedback } from '../hooks/useSubmitFeedback';

export default function QueryPage() {
  const { selectedProjectId } = useAppStore();
  const [query, setQuery] = useState('');
  const [screenshotBase64, setScreenshotBase64] = useState<string | undefined>();
  const chatStream = useChatQueryStream();
  const submitFeedback = useSubmitFeedback();

  const handleSubmit = () => {
    if (!selectedProjectId) return;
    void chatStream.start({ projectId: selectedProjectId, query, screenshotBase64 });
  };

  if (!selectedProjectId) {
    return (
      <div className="p-6 max-w-3xl mx-auto space-y-4">
        <h1 className="text-xl font-semibold">Ask a question</h1>
        <p className="text-sm text-gray-500">
          Select a project first on the <Link className="underline" to="/">home page</Link> before asking a question.
        </p>
      </div>
    );
  }

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
        disabled={!selectedProjectId || !query || chatStream.isStreaming}
      >
        Ask Agent
      </button>

      {chatStream.isStreaming && !chatStream.draft && (
        <p className="text-sm text-gray-500">Triage → Research → Analysis → Drafting...</p>
      )}

      {chatStream.draft && (
        <ResultsPanel
          result={{ draft: chatStream.draft, confidence: chatStream.confidence ?? 0, sources: chatStream.sources }}
          onCopy={() => navigator.clipboard.writeText(chatStream.draft)}
          onMarkUseful={(useful) => submitFeedback.mutate({ projectId: selectedProjectId, query, useful, escalated: false })}
          onEscalate={() => submitFeedback.mutate({ projectId: selectedProjectId, query, escalated: true })}
        />
      )}
      {chatStream.error && (
        <p className="text-sm text-red-600">
          Something went wrong. <button className="underline" onClick={chatStream.retry}>Retry</button>
        </p>
      )}
    </div>
  );
}
