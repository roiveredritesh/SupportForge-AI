import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { ProjectSelector } from './ProjectSelector';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn() } }));

describe('ProjectSelector', () => {
  it('renders project options once loaded', async () => {
    (apiClient.get as any).mockResolvedValue({ data: [{ id: 'proj1', name: 'Project One' }] });
    const client = new QueryClient();

    render(
      <QueryClientProvider client={client}>
        <ProjectSelector value={null} onChange={() => {}} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByText('Project One')).toBeInTheDocument());
  });
});
