import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MessageBubble } from './MessageBubble';

describe('MessageBubble', () => {
  it('renders assistant draft and confidence badge without citations', () => {
    render(
      <MessageBubble
        role="assistant"
        content="**Try restarting the service.**"
        confidence={0.85}
        sources={[{ label: 'KB: restart.md', url: 'kb/restart.md' }]}
      />,
    );

    expect(screen.getByText('Try restarting the service.')).toBeInTheDocument();
    expect(screen.getByText('High confidence')).toBeInTheDocument();
    expect(screen.queryByText('Sources:')).not.toBeInTheDocument();
    expect(screen.queryByText('KB: restart.md')).not.toBeInTheDocument();
  });

  it('renders assistant draft with no sources prop', () => {
    render(<MessageBubble role="assistant" content="**Restart it.**" confidence={0.85} />);

    expect(screen.getByText('Restart it.')).toBeInTheDocument();
    expect(screen.queryByText('Sources:')).not.toBeInTheDocument();
  });

  it('renders user messages as plain text without markdown or confidence', () => {
    render(<MessageBubble role="user" content="Why is the server down?" />);

    expect(screen.getByText('Why is the server down?')).toBeInTheDocument();
  });
});
