import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import LoginPage from './LoginPage';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({
  apiClient: { post: vi.fn().mockResolvedValue({ data: { accessToken: 'tok', expiresAt: '2026-08-07T00:00:00Z' } }) },
}));

function renderPage() {
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <LoginPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('LoginPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('submits the payload when both fields are filled', async () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith('/auth/token', {
        userName: 'alice',
        password: 'Passw0rd!',
      }),
    );
  });

  it('shows an inline error under the username field when username is empty and does not call login', () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    const usernameInput = screen.getByLabelText('Username');
    expect(usernameInput).toHaveAttribute('aria-describedby', 'userName-error');
    expect(document.getElementById('userName-error')).toHaveTextContent('This field is required.');
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('shows an inline error under the password field when password is empty and does not call login', () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(document.getElementById('password-error')).toHaveTextContent('This field is required.');
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('clears prior field errors once corrected and resubmitted', async () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(document.getElementById('userName-error')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(document.getElementById('userName-error')).not.toBeInTheDocument();
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));
  });

  it('fires exactly one login.mutate call when the submit button is double-clicked while pending', async () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });

    const button = screen.getByRole('button', { name: 'Sign in' });
    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));
  });
});
