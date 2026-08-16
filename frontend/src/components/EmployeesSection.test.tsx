import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, beforeEach, vi } from 'vitest';
import { EmployeesSection } from './EmployeesSection';
import { useAuthStore } from '../store/useAuthStore';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({
  apiClient: {
    get: vi.fn((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Org One' }] });
      return Promise.resolve({ data: [] });
    }),
    post: vi.fn().mockResolvedValue({ data: {} }),
  },
}));

function renderSection() {
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <EmployeesSection />
    </QueryClientProvider>,
  );
}

// U8: employee list renders as a card grid instead of a <ul>/<li> list.
describe('EmployeesSection grid layout', () => {
  beforeEach(() => {
    useAuthStore.setState({ role: 'Admin' });
  });

  it('renders 3 employees as cards inside a grid container', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Org One' }] });
      if (url === '/orgs/org1/employees')
        return Promise.resolve({
          data: [
            { id: 'e1', userName: 'alice', role: 'L1', projectIds: [] },
            { id: 'e2', userName: 'bob', role: 'L2', projectIds: ['p1'] },
            { id: 'e3', userName: 'carol', role: 'L3', projectIds: ['p1', 'p2'] },
          ],
        });
      return Promise.resolve({ data: [] });
    });

    renderSection();

    const alice = await screen.findByText(/alice/);
    expect(screen.getByText(/bob/)).toBeInTheDocument();
    expect(screen.getByText(/carol/)).toBeInTheDocument();

    const card = alice.closest('.rounded-lg');
    expect(card?.parentElement).toHaveClass('grid');
  });

  it('shows the empty-state text and no grid when there are zero employees', async () => {
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Org One' }] });
      if (url === '/orgs/org1/employees') return Promise.resolve({ data: [] });
      return Promise.resolve({ data: [] });
    });

    renderSection();

    expect(await screen.findByText('No employees registered yet.')).toBeInTheDocument();
    expect(document.querySelector('.grid')).toBeNull();
  });
});
