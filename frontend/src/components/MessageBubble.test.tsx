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
      />,
    );

    expect(screen.getByText('Try restarting the service.')).toBeInTheDocument();
    expect(screen.getByText('High confidence')).toBeInTheDocument();
    expect(screen.queryByText('Sources:')).not.toBeInTheDocument();
  });

  it('renders user messages as plain text without markdown or confidence', () => {
    render(<MessageBubble role="user" content="Why is the server down?" />);

    expect(screen.getByText('Why is the server down?')).toBeInTheDocument();
  });

  // E1 (gap-closing-solutions.md Phase E): sources are separate UI chrome, not merged into the
  // copyable draft content. Rendered as one deduped, comma-joined line rather than a list -- a
  // KB retrieval can cite the same page many times over, and a line per citation reads as clutter.
  it('renders sources as a single deduped comma-joined line', () => {
    render(
      <MessageBubble
        role="assistant"
        content="Try restarting the service."
        sources={[
          { label: 'KB: getting-started.md', url: 'kb/getting-started.md' },
          { label: 'KB: getting-started.md', url: 'kb/getting-started.md#2' },
          { label: 'Code: project graph', url: 'proj1' },
        ]}
      />,
    );

    expect(screen.getByText('Sources:')).toBeInTheDocument();
    expect(screen.getByText('KB: getting-started.md, Code: project graph')).toBeInTheDocument();
  });

  it('omits the Sources section when sources is an empty array', () => {
    render(<MessageBubble role="assistant" content="Try restarting the service." sources={[]} />);

    expect(screen.queryByText('Sources:')).not.toBeInTheDocument();
  });

  it('renders token usage alongside sources', () => {
    render(
      <MessageBubble
        role="assistant"
        content="Try restarting the service."
        sources={[{ label: 'KB: getting-started.md', url: 'kb/getting-started.md' }]}
        totalTokensUsed={8406}
      />,
    );

    expect(screen.getByText('Tokens used:')).toBeInTheDocument();
    expect(screen.getByText('8,406')).toBeInTheDocument();
  });
});
