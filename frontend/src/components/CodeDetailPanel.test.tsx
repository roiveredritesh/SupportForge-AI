import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CodeDetailPanel } from './CodeDetailPanel';

describe('CodeDetailPanel', () => {
  // U11/L1: neither codeDetails nor commitHistory present -- the backend never sends either field
  // to L1 callers, so this is what an L1 response's panel renders (nothing).
  it('renders nothing when codeDetails and commitHistory are both absent', () => {
    const { container } = render(<CodeDetailPanel />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing when both are empty arrays', () => {
    const { container } = render(<CodeDetailPanel codeDetails={[]} commitHistory={[]} />);
    expect(container).toBeEmptyDOMElement();
  });

  // U11/L2-L3: commit history renders under the code details once expanded, with author/date/message
  // and a working PR link when one was resolved.
  it('renders recent commits with author, date, message, and PR link when expanded', () => {
    render(
      <CodeDetailPanel
        codeDetails={['NODE Foo [src=Foo.cs loc=L10]']}
        commitHistory={[
          { sha: 'abc1234567', author: 'Jane Doe', date: '2026-01-15T00:00:00Z', message: 'fix null check', prUrl: 'https://github.com/acme/widget/pull/42' },
        ]}
      />,
    );

    fireEvent.click(screen.getByRole('button'));

    expect(screen.getByText('Recent commits')).toBeInTheDocument();
    expect(screen.getByText(/fix null check/)).toBeInTheDocument();
    expect(screen.getByText(/Jane Doe/)).toBeInTheDocument();
    const link = screen.getByRole('link', { name: 'PR' });
    expect(link).toHaveAttribute('href', 'https://github.com/acme/widget/pull/42');
  });

  // U11: graceful degradation on the frontend side too -- a commit with no resolved PR link (the
  // GitHub REST call failed or found nothing) still renders, just without a link.
  it('renders a commit without a PR link when none was resolved', () => {
    render(
      <CodeDetailPanel
        commitHistory={[{ sha: 'def4567890', author: 'Jane Doe', date: '2026-01-15T00:00:00Z', message: 'add logging' }]}
      />,
    );

    fireEvent.click(screen.getByRole('button'));

    expect(screen.getByText(/add logging/)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'PR' })).not.toBeInTheDocument();
  });

  it('counts codeDetails and commitHistory together in the toggle label', () => {
    render(
      <CodeDetailPanel
        codeDetails={['detail one']}
        commitHistory={[{ sha: 'abc', author: 'Jane', date: '2026-01-15T00:00:00Z', message: 'msg' }]}
      />,
    );

    expect(screen.getByText('Show code details (2)')).toBeInTheDocument();
  });
});
