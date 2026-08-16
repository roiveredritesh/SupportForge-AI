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

  it('shows a validation error and does not call the endpoint when a required field is empty', () => {
    renderPage();
    // Fill everything except Industry.
    fireEvent.change(screen.getByLabelText('Organization Name'), { target: { value: 'Acme Inc' } });
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'alice' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Passw0rd!' } });
    fireEvent.change(screen.getByLabelText('Contact Person'), { target: { value: 'Jane Doe' } });
    fireEvent.change(screen.getByLabelText('Contact Number'), { target: { value: '555-0100' } });

    fireEvent.click(screen.getByText('Create account'));

    expect(screen.getByText('Please fill in all required fields.')).toBeInTheDocument();
    expect(apiClient.post).not.toHaveBeenCalled();
  });
});
