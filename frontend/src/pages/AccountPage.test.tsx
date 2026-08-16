import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import AccountPage from './AccountPage';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { post: vi.fn() } }));

function renderPage() {
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <AccountPage />
    </QueryClientProvider>,
  );
}

describe('AccountPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('submits the entered current and new passwords', async () => {
    (apiClient.post as any).mockResolvedValue({ data: {} });
    renderPage();

    fireEvent.change(screen.getByLabelText('Current password'), { target: { value: 'Passw0rd!' } });
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'NewPassw0rd!' } });
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith('/auth/change-password', {
        currentPassword: 'Passw0rd!',
        newPassword: 'NewPassw0rd!',
      }),
    );
    expect(await screen.findByText('Password changed.')).toBeInTheDocument();
  });

  it('shows an error when the mutation fails', async () => {
    (apiClient.post as any).mockRejectedValue(new Error('wrong current password'));
    renderPage();

    fireEvent.change(screen.getByLabelText('Current password'), { target: { value: 'WrongPassword!' } });
    fireEvent.change(screen.getByLabelText('New password'), { target: { value: 'NewPassw0rd!' } });
    fireEvent.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByText(/Could not change your password/)).toBeInTheDocument();
  });
});
