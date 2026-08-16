import { fireEvent, render, screen, waitFor } from '@testing-library/react';
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

// U3: register-employee form inline validation and double-submit guard.
describe('EmployeesSection register-employee validation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ role: 'Admin' });
    vi.mocked(apiClient.get).mockImplementation((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Org One' }] });
      return Promise.resolve({ data: [] });
    });
    // clearAllMocks() resets call history but not a custom mockImplementation set by an earlier
    // test (e.g. the double-submit test's pending-promise override) -- restore the default
    // resolving behavior explicitly so each test starts from a clean baseline.
    vi.mocked(apiClient.post).mockResolvedValue({ data: {} });
  });

  // Covers AE1: password empty shows an inline error and does not call the mutation.
  it('shows an inline password error and does not register when password is empty', async () => {
    renderSection();

    fireEvent.change(await screen.findByLabelText('Username'), { target: { value: 'newuser' } });
    fireEvent.click(screen.getByRole('button', { name: 'Register Employee' }));

    expect(await screen.findByText(/at least 6 characters/)).toBeInTheDocument();
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('shows the min-length inline error for a 3-character password', async () => {
    renderSection();

    fireEvent.change(await screen.findByLabelText('Username'), { target: { value: 'newuser' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'abc' } });
    fireEvent.click(screen.getByRole('button', { name: 'Register Employee' }));

    expect(await screen.findByText(/at least 6 characters/)).toBeInTheDocument();
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  // Covers AE3: double-clicking while pending fires exactly one POST.
  it('fires exactly one POST when the register button is double-clicked while pending', async () => {
    let resolvePost: (value: unknown) => void = () => {};
    vi.mocked(apiClient.post).mockImplementation(
      () => new Promise((resolve) => { resolvePost = resolve; }),
    );
    renderSection();

    fireEvent.change(await screen.findByLabelText('Username'), { target: { value: 'newuser' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'longenough' } });

    const button = screen.getByRole('button', { name: 'Register Employee' });
    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));
    resolvePost({ data: {} });
  });

  it('clears prior errors and resets the form on a successful submit', async () => {
    renderSection();

    fireEvent.click(await screen.findByRole('button', { name: 'Register Employee' }));
    expect(await screen.findByText('This field is required.')).toBeInTheDocument();
    expect(await screen.findByText(/at least 6 characters/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'newuser' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'longenough' } });
    fireEvent.click(screen.getByRole('button', { name: 'Register Employee' }));

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.queryByText('This field is required.')).toBeNull());
    await waitFor(() =>
      expect((screen.getByLabelText('Username') as HTMLInputElement).value).toBe(''),
    );
  });
});
