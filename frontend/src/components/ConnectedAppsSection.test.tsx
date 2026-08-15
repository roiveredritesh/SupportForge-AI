import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { ConnectedAppsSection } from './ConnectedAppsSection';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } }));

function renderWithClient() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <ConnectedAppsSection />
    </QueryClientProvider>,
  );
}

describe('ConnectedAppsSection', () => {
  it('renders the connection list and per-tool checkboxes for the selected server', async () => {
    useAuthStore.setState({ role: 'Admin' });
    (apiClient.get as any).mockImplementation((url: string) => {
      if (url === '/orgs/org1/mcp-connections/catalog') {
        return Promise.resolve({
          data: [
            {
              serverType: 'github',
              tools: [
                { name: 'list_commits', description: 'Lists recent commits. Read-only.', minRole: 'L1' },
                { name: 'create_issue', description: 'Opens a new GitHub issue.', minRole: 'L2' },
              ],
            },
          ],
        });
      }
      if (url === '/orgs/org1/mcp-connections') {
        return Promise.resolve({ data: [{ serverType: 'github', enabledTools: ['list_commits'] }] });
      }
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      return Promise.resolve({ data: [] });
    });

    renderWithClient();

    // Existing connection is listed.
    await waitFor(() => expect(screen.getByText(/tools: list_commits/)).toBeInTheDocument());

    // Selecting the server in the connect form shows its tool checkboxes.
    const select = await screen.findByLabelText('Server');
    fireEvent.change(select, { target: { value: 'github' } });

    await waitFor(() => expect(screen.getByText('create_issue')).toBeInTheDocument());
    const checkboxes = screen.getAllByRole('checkbox');
    expect(checkboxes.length).toBeGreaterThan(0);
  });

  it('renders nothing when the caller is not an Admin', () => {
    useAuthStore.setState({ role: 'L1' });
    (apiClient.get as any).mockResolvedValue({ data: [{ id: 'org1', name: 'Acme' }] });

    const { container } = renderWithClient();

    expect(container).toBeEmptyDOMElement();
  });
});
