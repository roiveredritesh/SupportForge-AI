import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import SettingsProjectsPage from './SettingsProjectsPage';
import SettingsEmployeesPage from './SettingsEmployeesPage';
import SettingsConnectedAppsPage from './SettingsConnectedAppsPage';
import SettingsFeedbackPage from './SettingsFeedbackPage';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({
  apiClient: {
    get: vi.fn((url: string) => {
      if (url === '/orgs') return Promise.resolve({ data: [{ id: 'org1', name: 'Org One' }] });
      return Promise.resolve({ data: [] });
    }),
    post: vi.fn().mockResolvedValue({ data: {} }),
    delete: vi.fn().mockResolvedValue({ data: {} }),
  },
}));

// U3: /settings/* is a pure route split of the old /admin page -- each sub-page should render
// only its own section, the shared SettingsNav should highlight whichever is active, and each
// section's existing client-side RequireRole gate (Admin-only) must keep working unchanged.
function renderAt(initialPath: string) {
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[initialPath]}>
        <Routes>
          <Route path="/settings/projects" element={<SettingsProjectsPage />} />
          <Route path="/settings/employees" element={<SettingsEmployeesPage />} />
          <Route path="/settings/connected-apps" element={<SettingsConnectedAppsPage />} />
          <Route path="/settings/feedback" element={<SettingsFeedbackPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('Settings routed sub-pages', () => {
  beforeEach(() => {
    useAuthStore.setState({ role: 'Admin' });
  });

  it('renders only Employees content at /settings/employees, not Projects/Connected Apps/Feedback', async () => {
    renderAt('/settings/employees');

    expect(await screen.findByRole('heading', { name: 'Employees' })).toBeInTheDocument();
    expect(screen.queryByText('Add Project')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Connected Apps' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Feedback' })).not.toBeInTheDocument();
  });

  it('renders only Projects content at /settings/projects', () => {
    renderAt('/settings/projects');

    expect(screen.getByText('Add Project')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Employees' })).not.toBeInTheDocument();
  });

  it('hides Admin-only content (Connected Apps) for a non-Admin session via the existing RequireRole gate', () => {
    useAuthStore.setState({ role: 'L1' });
    renderAt('/settings/connected-apps');

    expect(screen.queryByRole('heading', { name: 'Connected Apps' })).not.toBeInTheDocument();
  });

  it('highlights the active section in the sub-nav', () => {
    renderAt('/settings/feedback');

    const feedbackLink = screen.getByRole('link', { name: 'Feedback' });
    const projectsLink = screen.getByRole('link', { name: 'Projects' });
    expect(feedbackLink).toHaveClass('text-indigo-600');
    expect(projectsLink).not.toHaveClass('text-indigo-600');
  });
});
