import { createElement, type ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { useRegisterEmployee } from './useRegisterEmployee';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { post: vi.fn() } }));

describe('useRegisterEmployee', () => {
  it('posts to the org-scoped employees endpoint without the orgId in the body', async () => {
    (apiClient.post as any).mockResolvedValue({ data: { id: 'user1' } });
    const client = new QueryClient();
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client }, children);

    const { result } = renderHook(() => useRegisterEmployee(), { wrapper });

    result.current.mutate({
      orgId: 'org1',
      userName: 'bob',
      password: 'Passw0rd!',
      role: 'L2',
      projectIds: ['proj1', 'proj2'],
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/orgs/org1/employees', {
      userName: 'bob',
      password: 'Passw0rd!',
      role: 'L2',
      projectIds: ['proj1', 'proj2'],
    });
  });

  it('invalidates the org employees query on success', async () => {
    (apiClient.post as any).mockResolvedValue({ data: { id: 'user1' } });
    const client = new QueryClient();
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client }, children);

    const { result } = renderHook(() => useRegisterEmployee(), { wrapper });

    result.current.mutate({ orgId: 'org1', userName: 'bob', password: 'Passw0rd!', role: 'L1', projectIds: [] });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['employees', 'org1'] });
  });
});
