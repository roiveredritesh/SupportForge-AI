import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import AdminPage from './AdminPage';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn().mockResolvedValue({ data: [] }), post: vi.fn().mockResolvedValue({ data: {} }) } }));

describe('AdminPage', () => {
  it('submits a new project with the entered id and name', async () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <AdminPage />
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
        <AdminPage />
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
        <AdminPage />
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
});
