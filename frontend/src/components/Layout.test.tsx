import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import Layout from './Layout';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({
  apiClient: { get: vi.fn().mockResolvedValue({ data: [] }) },
}));

function renderLayout() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<Layout />}>
            <Route path="/" element={<div>Page content</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('Layout responsive sidebar', () => {
  beforeEach(() => {
    useAuthStore.setState({ role: 'Admin' });
  });

  it('mobile nav drawer starts closed and opens via the menu button', () => {
    renderLayout();

    // The drawer's own nav landmark only mounts when open.
    expect(screen.queryByLabelText('Open navigation')).toBeInTheDocument();
    expect(screen.getAllByText('Dashboard')).toHaveLength(1); // only the desktop sidebar copy

    fireEvent.click(screen.getByLabelText('Open navigation'));

    // Now both the desktop (hidden via CSS, still in DOM) and drawer copies exist.
    expect(screen.getAllByText('Dashboard')).toHaveLength(2);
  });

  it('closes the mobile drawer when a nav link is clicked', () => {
    renderLayout();
    fireEvent.click(screen.getByLabelText('Open navigation'));
    expect(screen.getAllByText('Dashboard')).toHaveLength(2);

    fireEvent.click(screen.getAllByText('Dashboard')[1]);

    expect(screen.getAllByText('Dashboard')).toHaveLength(1);
  });

  it('closes the mobile drawer when the backdrop is clicked', () => {
    const { container } = renderLayout();
    fireEvent.click(screen.getByLabelText('Open navigation'));
    expect(screen.getAllByText('Dashboard')).toHaveLength(2);

    const backdrop = container.querySelector('[aria-hidden="true"]');
    expect(backdrop).not.toBeNull();
    fireEvent.click(backdrop!);

    expect(screen.getAllByText('Dashboard')).toHaveLength(1);
  });
});
