import { createElement, type ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { useInviteToConversation } from './useInviteToConversation';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { post: vi.fn() } }));

describe('useInviteToConversation', () => {
  it('posts the invited userId to the conversation invite endpoint', async () => {
    (apiClient.post as any).mockResolvedValue({ data: { id: 'conv1', invitedUserIds: ['eng1'] } });
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client }, children);

    const { result } = renderHook(() => useInviteToConversation(), { wrapper });

    result.current.mutate({ conversationId: 'conv1', projectId: 'proj1', userId: 'eng1' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/conversations/conv1/invite', { userId: 'eng1' });
  });

  it('invalidates the conversation list and detail queries on success', async () => {
    (apiClient.post as any).mockResolvedValue({ data: { id: 'conv1', invitedUserIds: ['eng1'] } });
    const client = new QueryClient();
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');
    const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client }, children);

    const { result } = renderHook(() => useInviteToConversation(), { wrapper });

    result.current.mutate({ conversationId: 'conv1', projectId: 'proj1', userId: 'eng1' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['conversations', 'proj1'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['conversation', 'conv1'] });
  });
});
