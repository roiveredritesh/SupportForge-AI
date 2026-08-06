import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useChatQueryStream } from './useChatQueryStream';
import { useAuthStore } from '../store/useAuthStore';

// Hand-rolled reader instead of a real Response/ReadableStream -- jsdom's Web Streams support is
// inconsistent, and the hook only ever calls res.body.getReader().read(), so this is both simpler
// and more portable than constructing a real stream.
function makeMockBody(chunks: string[]) {
  const encoder = new TextEncoder();
  let i = 0;
  return {
    getReader: () => ({
      read: async () => {
        if (i < chunks.length) {
          const value = encoder.encode(chunks[i]);
          i += 1;
          return { value, done: false };
        }
        return { value: undefined, done: true };
      },
    }),
  };
}

describe('useChatQueryStream', () => {
  beforeEach(() => {
    useAuthStore.setState({ accessToken: null, expiresAt: null });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('accumulates streamed tokens into draft, then applies the done event', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        body: makeMockBody([
          'data: "Hello"\n\n',
          'data: " world"\n\n',
          'event: done\ndata: {"confidence":0.8,"conversationId":"conv1","sources":[{"label":"KB: x.md","url":"kb/x.md"}]}\n\n',
        ]),
      }),
    );

    const { result } = renderHook(() => useChatQueryStream());

    await act(async () => {
      await result.current.start({ projectId: 'proj1', query: 'why' });
    });

    expect(result.current.draft).toBe('Hello world');
    expect(result.current.confidence).toBe(0.8);
    expect(result.current.conversationId).toBe('conv1');
    expect(result.current.sources).toEqual([{ label: 'KB: x.md', url: 'kb/x.md' }]);
    expect(result.current.isStreaming).toBe(false);
    expect(result.current.error).toBeUndefined();
  });

  it('surfaces a non-OK response as an error, not a thrown exception', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 500, body: null }));

    const { result } = renderHook(() => useChatQueryStream());

    await act(async () => {
      await result.current.start({ projectId: 'proj1', query: 'why' });
    });

    expect(result.current.error).toBeInstanceOf(Error);
    expect(result.current.isStreaming).toBe(false);
  });

  it('attaches the Authorization header when an access token is present', async () => {
    useAuthStore.setState({ accessToken: 'tok123', expiresAt: null });
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      body: makeMockBody(['event: done\ndata: {"confidence":0,"conversationId":"c","sources":[]}\n\n']),
    });
    vi.stubGlobal('fetch', fetchMock);

    const { result } = renderHook(() => useChatQueryStream());
    await act(async () => {
      await result.current.start({ projectId: 'proj1', query: 'why' });
    });

    const [, init] = fetchMock.mock.calls[0];
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer tok123');
  });

  it('omits the Authorization header when there is no access token', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      body: makeMockBody(['event: done\ndata: {"confidence":0,"conversationId":"c","sources":[]}\n\n']),
    });
    vi.stubGlobal('fetch', fetchMock);

    const { result } = renderHook(() => useChatQueryStream());
    await act(async () => {
      await result.current.start({ projectId: 'proj1', query: 'why' });
    });

    const [, init] = fetchMock.mock.calls[0];
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined();
  });

  it('reset clears draft, confidence, conversationId, and sources', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        body: makeMockBody([
          'data: "hi"\n\n',
          'event: done\ndata: {"confidence":0.5,"conversationId":"c1","sources":[{"label":"KB: x.md","url":"x"}]}\n\n',
        ]),
      }),
    );

    const { result } = renderHook(() => useChatQueryStream());
    await act(async () => {
      await result.current.start({ projectId: 'proj1', query: 'q' });
    });
    expect(result.current.draft).toBe('hi');

    act(() => result.current.reset());

    expect(result.current.draft).toBe('');
    expect(result.current.confidence).toBeUndefined();
    expect(result.current.conversationId).toBeUndefined();
    expect(result.current.sources).toEqual([]);
  });

  it('retry replays the last request', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      body: makeMockBody(['event: done\ndata: {"confidence":0.5,"conversationId":"c1","sources":[]}\n\n']),
    });
    vi.stubGlobal('fetch', fetchMock);

    const { result } = renderHook(() => useChatQueryStream());
    const request = { projectId: 'proj1', query: 'q' };
    await act(async () => {
      await result.current.start(request);
    });
    await act(async () => {
      result.current.retry();
    });

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const bodies = fetchMock.mock.calls.map(([, init]) => JSON.parse((init as RequestInit).body as string));
    expect(bodies[0]).toEqual(request);
    expect(bodies[1]).toEqual(request);
  });
});
