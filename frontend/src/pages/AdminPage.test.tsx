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

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/projects', expect.objectContaining({ id: 'proj2', name: 'Project Two' })));
  });
});
