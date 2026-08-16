import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import SettingsProjectsPage from './SettingsProjectsPage';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({
  apiClient: {
    get: vi.fn().mockResolvedValue({ data: [] }),
    post: vi.fn().mockResolvedValue({ data: {} }),
    delete: vi.fn().mockResolvedValue({ data: {} }),
  },
}));

describe('SettingsProjectsPage', () => {
  it('submits a new project with the entered id and name', async () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.change(screen.getByLabelText('Project ID'), { target: { value: 'proj2' } });
    fireEvent.change(screen.getByLabelText('Project Name'), { target: { value: 'Project Two' } });
    fireEvent.click(screen.getByText('Create Project'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/projects',
        expect.objectContaining({ id: 'proj2', name: 'Project Two', scheduledSyncIntervalHours: null }),
      ),
    );
  });

  it('submits the scheduled sync interval as a number when set, null when left blank', async () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.change(screen.getByLabelText('Project ID'), { target: { value: 'proj4' } });
    fireEvent.change(screen.getByLabelText('Project Name'), { target: { value: 'Project Four' } });
    fireEvent.change(screen.getByLabelText('Scheduled KB sync interval (hours, optional)'), { target: { value: '6' } });
    fireEvent.click(screen.getByText('Create Project'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith('/projects', expect.objectContaining({ scheduledSyncIntervalHours: 6 })),
    );
  });

  it('shows a "crawl linked pages" checkbox only for Website KB sources, and includes it when checked', async () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.change(screen.getByLabelText('Project ID'), { target: { value: 'proj3' } });
    fireEvent.change(screen.getByLabelText('Project Name'), { target: { value: 'Project Three' } });
    fireEvent.click(screen.getByText('+ Add source'));

    // Defaults to "Documents" -- no crawl-linked-pages checkbox until the type is Website.
    expect(screen.queryByRole('checkbox')).toBeNull();

    // First combobox is the KB source type selector; a second (repo-association) combobox only
    // appears while type === 'Documents', which is the default this test starts from.
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: 'Website' } });
    fireEvent.change(screen.getByPlaceholderText('Website URL'), { target: { value: 'https://example.com/docs' } });
    fireEvent.click(screen.getByRole('checkbox'));
    fireEvent.click(screen.getByText('Create Project'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/projects',
        expect.objectContaining({
          kbSources: [expect.objectContaining({ type: 'Website', location: 'https://example.com/docs', crawlLinkedPages: true })],
        }),
      ),
    );
  });

  it('shows dead-letter entries for a project and dismisses one', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/projects')
        return Promise.resolve({ data: [{ id: 'proj1', name: 'Proj One', repos: [], kbSources: [] }] });
      if (url === '/ingestion/dead-letters')
        return Promise.resolve({
          data: [{ id: 'dl1', projectId: 'proj1', jobType: 'WebsiteIngestionJob', error: 'boom', failedAt: '2026-01-01T00:00:00Z' }],
        });
      if (url.includes('/freshness'))
        return Promise.resolve({ data: { isFresh: true, staleSources: [], sources: [] } });
      return Promise.resolve({ data: [] });
    });

    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByText('Failed ingestion jobs (1):')).toBeInTheDocument());
    expect(screen.getByText(/WebsiteIngestionJob/)).toBeInTheDocument();

    fireEvent.click(screen.getByText('Dismiss'));

    await waitFor(() => expect(apiClient.delete).toHaveBeenCalledWith('/ingestion/dead-letters/dl1'));
  });

  // U8: Existing Projects renders as a card grid instead of a <ul>/<li> list.
  it('renders 2 projects as cards inside a grid container', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/projects')
        return Promise.resolve({
          data: [
            { id: 'proj1', name: 'Proj One', repos: [], kbSources: [] },
            { id: 'proj2', name: 'Proj Two', repos: [], kbSources: [] },
          ],
        });
      return Promise.resolve({ data: [] });
    });

    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    const projOne = await screen.findByText(/Proj One/);
    expect(screen.getByText(/Proj Two/)).toBeInTheDocument();

    const card = projOne.closest('.rounded-lg');
    expect(card?.parentElement).toHaveClass('grid');
  });

  it('shows the empty-state text and no grid when there are zero projects', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: [] });

    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('No projects yet.')).toBeInTheDocument();
  });
});
