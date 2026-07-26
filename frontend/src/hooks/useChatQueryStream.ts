import { useCallback, useRef, useState } from 'react';
import { apiClient } from '../lib/apiClient';
import type { ChatQueryRequest, Source } from './useChatQuery';

interface DoneEvent { confidence: number; sources: Source[]; conversationId: string; }

export function useChatQueryStream() {
  const [draft, setDraft] = useState('');
  const [confidence, setConfidence] = useState<number>();
  const [sources, setSources] = useState<Source[]>([]);
  const [conversationId, setConversationId] = useState<string>();
  const [isStreaming, setIsStreaming] = useState(false);
  const [error, setError] = useState<Error>();
  const requestRef = useRef<ChatQueryRequest | undefined>(undefined);

  const start = useCallback(async (request: ChatQueryRequest) => {
    requestRef.current = request;
    setDraft('');
    setConfidence(undefined);
    setSources([]);
    setError(undefined);
    setIsStreaming(true);

    try {
      const res = await fetch(`${apiClient.defaults.baseURL}/chat/query/stream`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request),
      });
      if (!res.ok || !res.body) throw new Error(`Request failed: ${res.status}`);

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        const events = buffer.split('\n\n');
        buffer = events.pop() ?? '';

        for (const evt of events) {
          const eventType = evt.match(/^event: (.+)$/m)?.[1];
          const dataLine = evt.match(/^data: (.+)$/m)?.[1];
          if (!dataLine) continue;

          if (eventType === 'done') {
            const payload: DoneEvent = JSON.parse(dataLine);
            setConfidence(payload.confidence);
            setSources(payload.sources);
            setConversationId(payload.conversationId);
          } else {
            setDraft((prev) => prev + (JSON.parse(dataLine) as string));
          }
        }
      }
    } catch (e) {
      setError(e instanceof Error ? e : new Error(String(e)));
    } finally {
      setIsStreaming(false);
    }
  }, []);

  const retry = useCallback(() => {
    if (requestRef.current) void start(requestRef.current);
  }, [start]);

  const reset = useCallback(() => {
    setDraft('');
    setConfidence(undefined);
    setSources([]);
    setConversationId(undefined);
    setError(undefined);
  }, []);

  return { draft, confidence, sources, conversationId, isStreaming, error, start, retry, reset };
}
