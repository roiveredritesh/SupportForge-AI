import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
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

  it('renders a GFM Markdown table as an actual table, not literal pipe text', () => {
    const table = '| Plan | Price |\n| --- | --- |\n| Pro | $49 |';
    render(<MessageBubble role="assistant" content={table} />);

    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getByText('Price')).toBeInTheDocument();
    expect(screen.getByText('$49')).toBeInTheDocument();
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

  // U17/U20: marking "not useful" reveals a reason-code select; submit is disabled until a reason
  // is chosen, and onMarkUseful only fires once one is picked.
  it('requires a reason code before submitting "not useful" feedback', () => {
    const onMarkUseful = vi.fn();
    render(
      <MessageBubble
        role="assistant"
        content="answer"
        actions={{ onCopy: vi.fn(), onMarkUseful, onEscalate: vi.fn() }}
      />,
    );

    fireEvent.click(screen.getByText('Not Useful'));
    const submit = screen.getByText('Submit');
    expect(submit).toBeDisabled();
    expect(onMarkUseful).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Reason:'), { target: { value: 'Incomplete' } });
    expect(submit).not.toBeDisabled();
    fireEvent.click(submit);

    expect(onMarkUseful).toHaveBeenCalledWith(false, 'Incomplete');
  });

  // U17: "useful" feedback never requires a reason code.
  it('marks useful without requiring a reason code', () => {
    const onMarkUseful = vi.fn();
    render(
      <MessageBubble
        role="assistant"
        content="answer"
        actions={{ onCopy: vi.fn(), onMarkUseful, onEscalate: vi.fn() }}
      />,
    );

    fireEvent.click(screen.getByText('Mark Useful'));

    expect(onMarkUseful).toHaveBeenCalledWith(true);
    expect(screen.queryByText('Submit')).not.toBeInTheDocument();
  });

  // U20: Escalate is visible for every role (MessageBubble itself has no role awareness -- the
  // backend/ChatPage gate what the caller ultimately sees) and confirms queuing without rendering
  // any Markdown/code detail -- onEscalate's resolved value is never displayed.
  it('confirms escalation after calling onEscalate, without showing any returned content', async () => {
    const onEscalate = vi.fn().mockResolvedValue({ escalationId: 'e1', status: 'Open' });
    render(
      <MessageBubble
        role="assistant"
        content="answer"
        actions={{ onCopy: vi.fn(), onMarkUseful: vi.fn(), onEscalate }}
      />,
    );

    fireEvent.click(screen.getByText('Escalate'));

    expect(onEscalate).toHaveBeenCalled();
    expect(await screen.findByText('Escalated to the support queue.')).toBeInTheDocument();
    expect(screen.queryByText('e1')).not.toBeInTheDocument();
  });
});
