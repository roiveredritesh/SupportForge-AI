import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { TokenExpiryAlert } from './TokenExpiryAlert';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn() } }));

function renderWithClient() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <TokenExpiryAlert />
    </QueryClientProvider>,
  );
}

function mockConnections(connections: Array<{ serverType: string; enabledTools: string[]; expiresAt: string | null }>) {
  (apiClient.get as any).mockImplementation((url: string) => {
    if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
    if (url === '/orgs/org1/mcp-connections') return Promise.resolve({ data: connections });
    return Promise.resolve({ data: [] });
  });
}

const daysFromNow = (days: number) => new Date(Date.now() + days * 24 * 60 * 60 * 1000).toISOString();

describe('TokenExpiryAlert', () => {
  it('renders "will expire" wording for a connection nearing expiry', async () => {
    useAuthStore.setState({ role: 'Admin' });
    mockConnections([{ serverType: 'github', enabledTools: [], expiresAt: daysFromNow(3) }]);

    renderWithClient();

    await waitFor(() => expect(screen.getByText(/will expire in 3 days/)).toBeInTheDocument());
  });

  it('renders "has expired" wording for a connection past its expiry', async () => {
    useAuthStore.setState({ role: 'Admin' });
    mockConnections([{ serverType: 'github', enabledTools: [], expiresAt: daysFromNow(-2) }]);

    renderWithClient();

    await waitFor(() => expect(screen.getByText(/has expired/)).toBeInTheDocument());
    expect(screen.queryByText(/will expire/)).not.toBeInTheDocument();
  });

  it('renders nothing when no connection is near expiry', async () => {
    useAuthStore.setState({ role: 'Admin' });
    mockConnections([{ serverType: 'github', enabledTools: [], expiresAt: daysFromNow(60) }]);

    const { container } = renderWithClient();

    await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith('/orgs/org1/mcp-connections'));
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing when there are no connections at all', async () => {
    useAuthStore.setState({ role: 'Admin' });
    mockConnections([]);

    const { container } = renderWithClient();

    await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith('/orgs/org1/mcp-connections'));
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing when the caller is not an Admin, even with an expiring connection', async () => {
    useAuthStore.setState({ role: 'L1' });
    mockConnections([{ serverType: 'github', enabledTools: [], expiresAt: daysFromNow(1) }]);

    const { container } = renderWithClient();

    await waitFor(() => expect(apiClient.get).toHaveBeenCalledWith('/orgs/org1/mcp-connections'));
    expect(container).toBeEmptyDOMElement();
  });
});
