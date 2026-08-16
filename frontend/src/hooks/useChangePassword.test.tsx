import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { useChangePassword } from './useChangePassword';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { post: vi.fn() } }));

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient();
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('useChangePassword', () => {
  it('posts the current and new passwords', async () => {
    (apiClient.post as any).mockResolvedValue({ data: {} });

    const { result } = renderHook(() => useChangePassword(), { wrapper });

    result.current.mutate({ currentPassword: 'old', newPassword: 'new' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/auth/change-password', {
      currentPassword: 'old',
      newPassword: 'new',
    });
  });

  it('surfaces failures via isError', async () => {
    (apiClient.post as any).mockRejectedValue(new Error('incorrect password'));

    const { result } = renderHook(() => useChangePassword(), { wrapper });

    result.current.mutate({ currentPassword: 'wrong', newPassword: 'new' });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});
