import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import DashboardPage from './DashboardPage';
import { apiClient } from '../lib/apiClient';
import { useAppStore } from '../store/useAppStore';

vi.mock('../lib/apiClient', () => ({
  apiClient: {
    get: vi.fn(),
  },
}));

function renderDashboard() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <DashboardPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('DashboardPage', () => {
  beforeEach(() => {
    vi.mocked(apiClient.get).mockReset();
    useAppStore.setState({ selectedProjectId: null });
  });

  it('with no project selected: shows no chart sections and fires no metric queries', () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/projects') return Promise.resolve({ data: [{ id: 'proj1', name: 'Proj One', repos: [], kbSources: [] }] });
      return Promise.reject(new Error(`unexpected GET ${url}`));
    });

    renderDashboard();

    expect(screen.queryByText('Query Volume (last 30 days)')).toBeNull();
    expect(screen.queryByText('Feedback Ratio')).toBeNull();
    expect(screen.queryByText('Escalations')).toBeNull();
    expect(screen.queryByText('Token Usage')).toBeNull();
    expect(apiClient.get).not.toHaveBeenCalledWith(expect.stringContaining('query-volume'));
    expect(apiClient.get).not.toHaveBeenCalledWith(expect.stringContaining('feedback-summary'));
    expect(apiClient.get).not.toHaveBeenCalledWith(expect.stringContaining('escalation-stats'));
    expect(apiClient.get).not.toHaveBeenCalledWith(expect.stringContaining('token-usage'));
  });

  it('with a project selected and data present: renders all four chart sections', async () => {
    useAppStore.setState({ selectedProjectId: 'proj1' });
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/projects') return Promise.resolve({ data: [{ id: 'proj1', name: 'Proj One', repos: [], kbSources: [] }] });
      if (url === '/projects/proj1/query-volume')
        return Promise.resolve({ data: [{ date: '2026-08-01', count: 3 }, { date: '2026-08-02', count: 5 }] });
      if (url === '/projects/proj1/feedback-summary') return Promise.resolve({ data: { useful: 7, notUseful: 2 } });
      if (url === '/projects/proj1/escalation-stats') return Promise.resolve({ data: { open: 1, claimed: 2, resolved: 3 } });
      if (url === '/projects/proj1/token-usage') return Promise.resolve({ data: { total: 300, bySource: { chat: 200, ingestion: 100 } } });
      return Promise.reject(new Error(`unexpected GET ${url}`));
    });

    renderDashboard();

    await waitFor(() => expect(screen.getByText('Query Volume (last 30 days)')).toBeInTheDocument());
    expect(screen.getByText('Feedback Ratio')).toBeInTheDocument();
    expect(screen.getByText('Escalations')).toBeInTheDocument();
    expect(screen.getByText('Token Usage')).toBeInTheDocument();

    await waitFor(() => expect(screen.getByText('Chat: 200')).toBeInTheDocument());
    expect(screen.getByText('Ingestion: 100')).toBeInTheDocument();
    expect(screen.getByText('1')).toBeInTheDocument(); // Open count
  });

  it('with a project selected but zero data: renders empty states, not errors', async () => {
    useAppStore.setState({ selectedProjectId: 'proj-empty' });
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/projects') return Promise.resolve({ data: [{ id: 'proj-empty', name: 'Empty Project', repos: [], kbSources: [] }] });
      if (url === '/projects/proj-empty/query-volume') return Promise.resolve({ data: [] });
      if (url === '/projects/proj-empty/feedback-summary') return Promise.resolve({ data: { useful: 0, notUseful: 0 } });
      if (url === '/projects/proj-empty/escalation-stats') return Promise.resolve({ data: { open: 0, claimed: 0, resolved: 0 } });
      if (url === '/projects/proj-empty/token-usage') return Promise.resolve({ data: { total: 0, bySource: {} } });
      return Promise.reject(new Error(`unexpected GET ${url}`));
    });

    renderDashboard();

    await waitFor(() => expect(screen.getByText('No queries recorded yet.')).toBeInTheDocument());
    expect(screen.getByText('No feedback recorded yet.')).toBeInTheDocument();
    expect(screen.getByText('No token usage recorded yet.')).toBeInTheDocument();
  });
});
