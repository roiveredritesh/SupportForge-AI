import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { RequireRole } from './RequireRole';
import { useAuthStore } from '../store/useAuthStore';

describe('RequireRole', () => {
  it('renders children when the auth context role matches', () => {
    useAuthStore.setState({ role: 'Admin' });

    render(
      <RequireRole role="Admin">
        <p>Admin-only content</p>
      </RequireRole>,
    );

    expect(screen.getByText('Admin-only content')).toBeInTheDocument();
  });

  it('renders nothing when the role does not match', () => {
    useAuthStore.setState({ role: 'L1' });

    const { container } = render(
      <RequireRole role="Admin">
        <p>Admin-only content</p>
      </RequireRole>,
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing when no role is set', () => {
    useAuthStore.setState({ role: null });

    const { container } = render(
      <RequireRole role="Admin">
        <p>Admin-only content</p>
      </RequireRole>,
    );

    expect(container).toBeEmptyDOMElement();
  });
});
