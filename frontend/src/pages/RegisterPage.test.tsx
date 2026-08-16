import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import RegisterPage from './RegisterPage';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({
  apiClient: { post: vi.fn().mockResolvedValue({ data: { accessToken: 'tok', expiresAt: '2026-08-07T00:00:00Z' } }) },
}));

function renderPage() {
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <RegisterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function fillRequiredFields() {
  fireEvent.change(screen.getByLabelText('Organization Name'), { target: { value: 'Acme Inc' } });
  fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });
  fireEvent.change(screen.getByLabelText('Contact Person'), { target: { value: 'Jane Doe' } });
  fireEvent.change(screen.getByLabelText('Contact Number'), { target: { value: '555-0100' } });
  fireEvent.change(screen.getByLabelText('Industry'), { target: { value: 'Software' } });
}

describe('RegisterPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('submits the full payload when all required fields are filled, address omitted', async () => {
    renderPage();
    fillRequiredFields();

    fireEvent.click(screen.getByText('Create account'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith('/auth/register', {
        orgName: 'Acme Inc',
        userName: 'alice',
        password: 'Passw0rd!',
        contactPerson: 'Jane Doe',
        contactNumber: '555-0100',
        industry: 'Software',
        address: undefined,
      }),
    );
  });

  it('includes address when provided', async () => {
    renderPage();
    fillRequiredFields();
    fireEvent.change(screen.getByLabelText('Address (optional)'), { target: { value: '123 Main St' } });

    fireEvent.click(screen.getByText('Create account'));

    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/auth/register',
        expect.objectContaining({ address: '123 Main St' }),
      ),
    );
  });

  it('shows an inline error under the specific empty field and does not call the endpoint', () => {
    renderPage();
    // Fill everything except Industry.
    fireEvent.change(screen.getByLabelText('Organization Name'), { target: { value: 'Acme Inc' } });
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });
    fireEvent.change(screen.getByLabelText('Contact Person'), { target: { value: 'Jane Doe' } });
    fireEvent.change(screen.getByLabelText('Contact Number'), { target: { value: '555-0100' } });

    fireEvent.click(screen.getByText('Create account'));

    expect(screen.getByText('This field is required.')).toBeInTheDocument();
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('shows an inline error under the password field when password is empty', () => {
    renderPage();
    fireEvent.change(screen.getByLabelText('Organization Name'), { target: { value: 'Acme Inc' } });
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText('Contact Person'), { target: { value: 'Jane Doe' } });
    fireEvent.change(screen.getByLabelText('Contact Number'), { target: { value: '555-0100' } });
    fireEvent.change(screen.getByLabelText('Industry'), { target: { value: 'Software' } });

    fireEvent.click(screen.getByText('Create account'));

    const passwordInput = screen.getByLabelText('Password');
    expect(passwordInput).toHaveAttribute('aria-describedby', 'password-error');
    expect(document.getElementById('password-error')).toHaveTextContent(
      'Password must be at least 6 characters.',
    );
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('shows the min-length inline error under the password field for a 3-character password', () => {
    renderPage();
    fillRequiredFields();
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'abc' } });

    fireEvent.click(screen.getByText('Create account'));

    expect(document.getElementById('password-error')).toHaveTextContent(
      'Password must be at least 6 characters.',
    );
    expect(apiClient.post).not.toHaveBeenCalled();
  });

  it('clears prior field errors and calls register.mutate once all required fields are valid', async () => {
    renderPage();
    // First submit with password empty to populate an error.
    fireEvent.change(screen.getByLabelText('Organization Name'), { target: { value: 'Acme Inc' } });
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText('Contact Person'), { target: { value: 'Jane Doe' } });
    fireEvent.change(screen.getByLabelText('Contact Number'), { target: { value: '555-0100' } });
    fireEvent.change(screen.getByLabelText('Industry'), { target: { value: 'Software' } });
    fireEvent.click(screen.getByText('Create account'));
    expect(document.getElementById('password-error')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });
    fireEvent.click(screen.getByText('Create account'));

    expect(document.getElementById('password-error')).not.toBeInTheDocument();
    await waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));
  });

  it('fires exactly one register.mutate call when the submit button is double-clicked while pending', async () => {
    renderPage();
    fillRequiredFields();

    const button = screen.getByText('Create account');
    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledTimes(1));
  });

  it('surfaces the server-returned validation messages instead of a hardcoded guess', async () => {
    vi.mocked(apiClient.post).mockRejectedValueOnce({
      isAxiosError: true,
      response: {
        data: [
          'Passwords must have at least one non alphanumeric character.',
          "Passwords must have at least one uppercase ('A'-'Z').",
        ],
      },
    });
    renderPage();
    fillRequiredFields();

    fireEvent.click(screen.getByText('Create account'));

    expect(
      await screen.findByText('Passwords must have at least one non alphanumeric character.'),
    ).toBeInTheDocument();
    expect(screen.getByText("Passwords must have at least one uppercase ('A'-'Z').")).toBeInTheDocument();
    expect(screen.queryByText(/username may already be taken/)).not.toBeInTheDocument();
  });

  it('falls back to the generic message when the server error has no usable body', async () => {
    vi.mocked(apiClient.post).mockRejectedValueOnce({ isAxiosError: true, response: undefined });
    renderPage();
    fillRequiredFields();

    fireEvent.click(screen.getByText('Create account'));

    expect(await screen.findByText(/username may already be taken/)).toBeInTheDocument();
  });
});
