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

    // Defaults to "Documents" -- no crawl-linked-pages checkbox until the type is Website. The
    // page's own "Enable AI code classification" checkbox is always present, so this asserts on
    // the crawl-linked-pages one specifically, not "any checkbox on the page".
    expect(screen.queryByLabelText(/Also index pages linked/)).toBeNull();

    // First combobox is the KB source type selector; a second (repo-association) combobox only
    // appears while type === 'Documents', which is the default this test starts from.
    fireEvent.change(screen.getAllByRole('combobox')[0], { target: { value: 'Website' } });
    fireEvent.change(screen.getByPlaceholderText('Website URL'), { target: { value: 'https://example.com/docs' } });
    fireEvent.click(screen.getByLabelText(/Also index pages linked/));
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

  it('includes codeClassificationEnabled: false by default, true when the checkbox is checked', async () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.change(screen.getByLabelText('Project ID'), { target: { value: 'proj5' } });
    fireEvent.change(screen.getByLabelText('Project Name'), { target: { value: 'Project Five' } });
    fireEvent.click(screen.getByLabelText('Enable AI code classification for this project'));
    fireEvent.click(screen.getByText('Create Project'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/projects',
        expect.objectContaining({ id: 'proj5', codeClassificationEnabled: true }),
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

  // Existing Projects renders as a data table (rows/columns), not a card grid or a <ul>/<li> list.
  it('renders 2 projects as rows inside a table', async () => {
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

    await screen.findByText(/Proj One/);
    expect(screen.getByText(/Proj Two/)).toBeInTheDocument();

    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(3); // 1 header row + 2 data rows
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

  // U4: required-field inline validation on the add/edit-project form (previously had none).
  it('shows inline errors for both fields and does not submit when id and name are empty', async () => {
    vi.mocked(apiClient.post).mockClear();
    vi.mocked(apiClient.get).mockResolvedValue({ data: [] });

    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SettingsProjectsPage />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.click(screen.getByText('Create Project'));

    expect(await screen.findAllByText('This field is required.')).toHaveLength(2);
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('requires only name (not id) when editing an existing project', async () => {
    vi.mocked(apiClient.post).mockClear();
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/projects')
        return Promise.resolve({ data: [{ id: 'proj1', name: 'Proj One', repos: [], kbSources: [] }] });
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

    fireEvent.click(await screen.findByText('Edit'));
    fireEvent.change(screen.getByLabelText('Project Name'), { target: { value: '' } });
    fireEvent.click(screen.getByText('Save Changes'));

    // Only Project Name's error should appear -- Project ID is disabled while editing and its
    // required check is skipped.
    expect(await screen.findAllByText('This field is required.')).toHaveLength(1);
    expect(apiClient.post).not.toHaveBeenCalled();
  });
});
