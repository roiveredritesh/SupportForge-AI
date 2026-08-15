import { createElement, type ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { useConnectMcpServer, useMcpConnections } from './useMcpConnections';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } }));

function makeWrapper() {
  const client = new QueryClient();
  const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client }, children);
  return { client, wrapper };
}

describe('useMcpConnections', () => {
  it('fetches the org-scoped connections list', async () => {
    (apiClient.get as any).mockResolvedValue({ data: [{ serverType: 'github', enabledTools: ['list_commits'] }] });
    const { wrapper } = makeWrapper();

    const { result } = renderHook(() => useMcpConnections('org1'), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.get).toHaveBeenCalledWith('/orgs/org1/mcp-connections');
    expect(result.current.data).toEqual([{ serverType: 'github', enabledTools: ['list_commits'] }]);
  });
});

describe('useConnectMcpServer', () => {
  it('posts to the org-scoped mcp-connections endpoint without orgId in the body', async () => {
    (apiClient.post as any).mockResolvedValue({ data: { serverType: 'github', enabledTools: ['list_commits'] } });
    const { wrapper } = makeWrapper();

    const { result } = renderHook(() => useConnectMcpServer(), { wrapper });

    result.current.mutate({ orgId: 'org1', serverType: 'github', credential: 'ghp_secret', enabledTools: ['list_commits'] });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(apiClient.post).toHaveBeenCalledWith('/orgs/org1/mcp-connections', {
      serverType: 'github',
      credential: 'ghp_secret',
      enabledTools: ['list_commits'],
    });
  });

  it('invalidates the org connections query on success', async () => {
    (apiClient.post as any).mockResolvedValue({ data: { serverType: 'github', enabledTools: [] } });
    const { client, wrapper } = makeWrapper();
    const invalidateSpy = vi.spyOn(client, 'invalidateQueries');

    const { result } = renderHook(() => useConnectMcpServer(), { wrapper });

    result.current.mutate({ orgId: 'org1', serverType: 'github', credential: 'ghp_secret', enabledTools: [] });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['mcp-connections', 'org1'] });
  });
});
