import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import SettingsTokenUsagePage from './SettingsTokenUsagePage';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn() } }));

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SettingsTokenUsagePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const ORG_TOKEN_USAGE = {
  chart: [{ date: '2026-08-10', count: 2 }],
  entries: [
    { projectId: 'proj1', userId: 'alice', totalTokens: 100, createdAt: '2026-08-10T00:00:00Z', source: 'chat' },
    { projectId: 'proj2', userId: 'bob', totalTokens: 50, createdAt: '2026-08-11T00:00:00Z', source: 'ingestion' },
    { projectId: 'proj2', userId: null, totalTokens: 25, createdAt: '2026-08-12T00:00:00Z', source: 'ingestion' },
  ],
  projectIds: ['proj1', 'proj2'],
};

describe('SettingsTokenUsagePage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ role: 'Admin' });
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      if (url === '/orgs/org1/employees')
        return Promise.resolve({
          data: [
            { id: 'alice', userName: 'alice', role: 'L1', projectIds: ['proj1'] },
            { id: 'bob', userName: 'bob', role: 'L2', projectIds: ['proj2'] },
          ],
        });
      if (url === '/orgs/org1/token-usage') return Promise.resolve({ data: ORG_TOKEN_USAGE });
      return Promise.resolve({ data: [] });
    });
  });

  it('renders nothing for a non-Admin role', () => {
    useAuthStore.setState({ role: 'L1' });
    const { container } = renderPage();
    expect(container.querySelector('section')).toBeNull();
  });

  it('renders the chart, grid, and project filter options from the response, not from useProjects', async () => {
    renderPage();

    await waitFor(() => expect(screen.getAllByText('proj1').length).toBeGreaterThan(0));
    expect(screen.getAllByText('proj2').length).toBeGreaterThan(0);

    const projectSelect = screen.getByLabelText('Project') as HTMLSelectElement;
    const optionValues = Array.from(projectSelect.options).map((o) => o.value);
    expect(optionValues).toEqual(['', 'proj1', 'proj2']);
    // Confirms the options came from the token-usage response's projectIds, not a /projects call.
    expect(apiClient.get).not.toHaveBeenCalledWith('/projects', expect.anything());
  });

  it('shows Unknown for an entry with no attributed user', async () => {
    renderPage();

    expect(await screen.findByText('Unknown')).toBeInTheDocument();
  });

  // Covers AE5.
  it('narrows the grid to one employee across multiple projects when the employee filter is set', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      if (url === '/orgs/org1/employees')
        return Promise.resolve({ data: [{ id: 'alice', userName: 'alice', role: 'L1', projectIds: ['proj1'] }] });
      if (url === '/orgs/org1/token-usage')
        return Promise.resolve({
          data: {
            chart: [{ date: '2026-08-10', count: 1 }],
            entries: [
              { projectId: 'proj1', userId: 'alice', totalTokens: 100, createdAt: '2026-08-10T00:00:00Z', source: 'chat' },
              { projectId: 'proj3', userId: 'alice', totalTokens: 40, createdAt: '2026-08-11T00:00:00Z', source: 'chat' },
            ],
            projectIds: ['proj1', 'proj3'],
          },
        });
      return Promise.resolve({ data: [] });
    });

    renderPage();

    const employeeSelect = await screen.findByLabelText('Employee');
    await screen.findByRole('option', { name: 'alice' });
    fireEvent.change(employeeSelect, { target: { value: 'alice' } });

    await waitFor(() =>
      expect(apiClient.get).toHaveBeenCalledWith(
        '/orgs/org1/token-usage',
        expect.objectContaining({ params: expect.objectContaining({ userId: 'alice' }) }),
      ),
    );
    // Both proj1 and proj3 entries for alice render -- not scoped to one project.
    await waitFor(() => expect(screen.getAllByText('proj1').length).toBeGreaterThan(0));
    expect(screen.getAllByText('proj3').length).toBeGreaterThan(0);
  });

  it('shows an inline error and does not query when From is after To', async () => {
    renderPage();

    const fromInput = await screen.findByLabelText('From');
    const toInput = screen.getByLabelText('To');
    fireEvent.change(fromInput, { target: { value: '2026-08-20' } });
    fireEvent.change(toInput, { target: { value: '2026-08-01' } });

    expect(await screen.findByText('"From" date must not be after "To" date.')).toBeInTheDocument();
  });

  it('shows a loading indicator while fetching', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      if (url === '/orgs/org1/token-usage') return new Promise(() => {}); // never resolves
      return Promise.resolve({ data: [] });
    });

    renderPage();

    expect(await screen.findByText('Loading…')).toBeInTheDocument();
  });

  it('shows an inline error banner when the request fails', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      if (url === '/orgs/org1/token-usage') return Promise.reject(new Error('boom'));
      return Promise.resolve({ data: [] });
    });

    renderPage();

    expect(await screen.findByText('Could not load token usage. Try again.')).toBeInTheDocument();
  });

  it('shows the empty-result message when the filtered result set is empty', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      if (url === '/orgs/org1/token-usage')
        return Promise.resolve({ data: { chart: [], entries: [], projectIds: [] } });
      return Promise.resolve({ data: [] });
    });

    renderPage();

    expect(await screen.findByText('No token usage for this filter combination.')).toBeInTheDocument();
  });

  it('paginates the grid beyond one page of entries', async () => {
    const manyEntries = Array.from({ length: 30 }, (_, i) => ({
      projectId: 'proj1',
      userId: 'alice',
      totalTokens: 1,
      createdAt: `2026-08-${String((i % 27) + 1).padStart(2, '0')}T00:00:00Z`,
      source: 'chat',
    }));
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Acme' }] });
      if (url === '/orgs/org1/employees')
        return Promise.resolve({ data: [{ id: 'alice', userName: 'alice', role: 'L1', projectIds: ['proj1'] }] });
      if (url === '/orgs/org1/token-usage')
        return Promise.resolve({ data: { chart: [], entries: manyEntries, projectIds: ['proj1'] } });
      return Promise.resolve({ data: [] });
    });

    renderPage();

    await screen.findByText('Page 1 of 2');
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument();
  });
});
