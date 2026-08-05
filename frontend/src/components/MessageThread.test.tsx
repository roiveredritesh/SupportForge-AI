import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MessageThread } from './MessageThread';

describe('MessageThread', () => {
  it('shows a thinking indicator for a pending turn before any draft text has arrived', () => {
    render(<MessageThread messages={[]} pending={{ query: 'Why is the server down?', draft: '' }} />);

    expect(screen.getByText('Why is the server down?')).toBeInTheDocument();
    expect(screen.getByText('Thinking…')).toBeInTheDocument();
  });

  it('replaces the thinking indicator with the assistant bubble once draft text streams in', () => {
    render(<MessageThread messages={[]} pending={{ query: 'Why is the server down?', draft: 'Try restarting it.' }} />);

    expect(screen.queryByText('Thinking…')).not.toBeInTheDocument();
    expect(screen.getByText('Try restarting it.')).toBeInTheDocument();
  });
});
