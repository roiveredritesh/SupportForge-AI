import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { PresenceIndicator } from './PresenceIndicator';

describe('PresenceIndicator', () => {
  it('renders nothing when no one is present', () => {
    const { container } = render(<PresenceIndicator participantIds={[]} />);

    expect(container).toBeEmptyDOMElement();
  });

  it('renders an avatar per participant when two clients join the same conversation', () => {
    render(<PresenceIndicator participantIds={['alice-id', 'bob-id']} />);

    expect(screen.getByTitle('alice-id')).toBeInTheDocument();
    expect(screen.getByTitle('bob-id')).toBeInTheDocument();
  });
});
