import { renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { usePresence } from './usePresence';

const handlers: Record<string, (payload: unknown) => void> = {};
const invoke = vi.fn().mockResolvedValue(undefined);
const start = vi.fn().mockResolvedValue(undefined);
const stop = vi.fn().mockResolvedValue(undefined);
const on = vi.fn((event: string, handler: (payload: unknown) => void) => {
  handlers[event] = handler;
});

vi.mock('@microsoft/signalr', () => ({
  // Arrow functions can't be `new`-ed, so this needs a real function/class to stand in for
  // signalR.HubConnectionBuilder (usePresence.ts does `new signalR.HubConnectionBuilder()`).
  HubConnectionBuilder: vi.fn().mockImplementation(function (this: unknown) {
    return {
      withUrl: vi.fn().mockReturnThis(),
      withAutomaticReconnect: vi.fn().mockReturnThis(),
      build: vi.fn().mockReturnValue({ on, start, stop, invoke }),
    };
  }),
}));

vi.mock('../lib/apiClient', () => ({ apiClient: { defaults: { baseURL: 'http://localhost/api' } } }));

describe('usePresence', () => {
  beforeEach(() => {
    invoke.mockClear();
    start.mockClear();
    stop.mockClear();
    on.mockClear();
  });

  it('joins the conversation and starts with no participants', async () => {
    const { result } = renderHook(() => usePresence('conv1'));

    await waitFor(() => expect(start).toHaveBeenCalled());
    await waitFor(() => expect(invoke).toHaveBeenCalledWith('JoinConversation', 'conv1'));
    expect(result.current.participantIds).toEqual([]);
  });

  it('updates participantIds when two clients join and a presence event arrives', async () => {
    const { result } = renderHook(() => usePresence('conv1'));

    await waitFor(() => expect(handlers.presence).toBeDefined());
    handlers.presence({ conversationId: 'conv1', userIds: ['alice', 'bob'] });

    await waitFor(() => expect(result.current.participantIds).toEqual(['alice', 'bob']));
  });

  it('ignores a presence event for a different conversation', async () => {
    const { result } = renderHook(() => usePresence('conv1'));

    await waitFor(() => expect(handlers.presence).toBeDefined());
    handlers.presence({ conversationId: 'conv-other', userIds: ['carol'] });

    expect(result.current.participantIds).toEqual([]);
  });

  it('leaves the conversation and stops the connection on unmount', async () => {
    const { unmount } = renderHook(() => usePresence('conv1'));
    await waitFor(() => expect(start).toHaveBeenCalled());

    unmount();

    await waitFor(() => expect(invoke).toHaveBeenCalledWith('LeaveConversation', 'conv1'));
    await waitFor(() => expect(stop).toHaveBeenCalled());
  });

  it('does not connect when conversationId is null', () => {
    const { result } = renderHook(() => usePresence(null));

    expect(start).not.toHaveBeenCalled();
    expect(result.current.participantIds).toEqual([]);
  });
});
