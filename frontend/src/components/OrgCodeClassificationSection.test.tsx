import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { OrgCodeClassificationSection } from './OrgCodeClassificationSection';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn(), post: vi.fn().mockResolvedValue({ data: {} }) } }));

function renderWithClient() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <OrgCodeClassificationSection />
    </QueryClientProvider>,
  );
}

describe('OrgCodeClassificationSection', () => {
  it('renders nothing for a non-Admin role', async () => {
    useAuthStore.setState({ role: 'L1' });
    (apiClient.get as any).mockResolvedValue({ data: [{ id: 'org1', name: 'Acme', codeClassificationEnabled: false }] });

    const { container } = renderWithClient();

    await waitFor(() => expect(apiClient.get).toHaveBeenCalled());
    expect(container.firstChild).toBeNull();
  });

  it('Admin sees the checkbox reflecting the org value and can save a change', async () => {
    useAuthStore.setState({ role: 'Admin' });
    (apiClient.get as any).mockResolvedValue({
      data: [{ id: 'org1', name: 'Acme', contactPerson: 'Jane', contactNumber: '555', industry: 'Tech', codeClassificationEnabled: false }],
    });

    renderWithClient();

    const checkbox = await screen.findByLabelText('Enable AI code classification for this org');
    expect(checkbox).not.toBeChecked();

    fireEvent.click(checkbox);
    fireEvent.click(screen.getByText('Save'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/orgs',
        expect.objectContaining({ id: 'org1', codeClassificationEnabled: true }),
      ),
    );
  });
});
