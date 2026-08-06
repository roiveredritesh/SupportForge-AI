import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useLogin } from './useLogin';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({ apiClient: { post: vi.fn() } }));

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient();
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('useLogin', () => {
  beforeEach(() => {
    useAuthStore.setState({ accessToken: null, expiresAt: null });
  });

  it('posts credentials and stores the returned token on success', async () => {
    (apiClient.post as any).mockResolvedValue({
      data: { accessToken: 'tok123', expiresAt: '2026-08-07T00:00:00Z' },
    });

    const { result } = renderHook(() => useLogin(), { wrapper });

    result.current.mutate({ userName: 'alice', password: 'pw' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/auth/token', { userName: 'alice', password: 'pw' });
    expect(useAuthStore.getState().accessToken).toBe('tok123');
    expect(useAuthStore.getState().expiresAt).toBe('2026-08-07T00:00:00Z');
  });

  it('does not touch the auth store when the request fails', async () => {
    (apiClient.post as any).mockRejectedValue(new Error('invalid credentials'));

    const { result } = renderHook(() => useLogin(), { wrapper });

    result.current.mutate({ userName: 'alice', password: 'wrong' });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(useAuthStore.getState().accessToken).toBeNull();
  });
});
