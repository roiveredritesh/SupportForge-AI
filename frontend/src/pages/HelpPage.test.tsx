import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import HelpPage from './HelpPage';
import { useAuthStore } from '../store/useAuthStore';

describe('HelpPage', () => {
  it('shows only L1-relevant content for an L1 session', () => {
    useAuthStore.setState({ role: 'L1' });

    render(<HelpPage />);

    expect(screen.getByText(/Ask a question/)).toBeInTheDocument();
    expect(screen.queryByText(/Claim an escalation/)).not.toBeInTheDocument();
    expect(screen.queryByText(/register an employee/i)).not.toBeInTheDocument();
  });

  it('shows the full content set for an Admin session', () => {
    useAuthStore.setState({ role: 'Admin' });

    render(<HelpPage />);

    expect(screen.getByText(/Ask a question/)).toBeInTheDocument();
    expect(screen.getByText(/Claim an escalation/)).toBeInTheDocument();
    expect(screen.getByText(/register an employee/i)).toBeInTheDocument();
  });
});
