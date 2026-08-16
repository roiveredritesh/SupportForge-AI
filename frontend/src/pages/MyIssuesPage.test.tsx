import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import MyIssuesPage from './MyIssuesPage';
import type { Escalation } from '../hooks/useEscalations';

let issuesData: Escalation[] = [];

vi.mock('../hooks/useEscalations', () => ({
  useMyIssues: () => ({ data: issuesData, isLoading: false }),
}));

// U21: the calling engineer's claimed-open + recently-resolved escalations, with a status filter.
describe('MyIssuesPage', () => {
  it('filters between claimed and resolved issues', () => {
    issuesData = [
      { id: 'e1', conversationId: 'conv1', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Claimed', markdown: 'md1', claimedByUserId: 'l3', claimedAt: new Date().toISOString() },
      { id: 'e2', conversationId: 'conv2', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Resolved', markdown: 'md2', claimedByUserId: 'l3', claimedAt: new Date().toISOString() },
    ];

    render(<MyIssuesPage />);

    expect(screen.getByText(/conv1/)).toBeInTheDocument();
    expect(screen.getByText(/conv2/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Claimed' }));
    expect(screen.getByText(/conv1/)).toBeInTheDocument();
    expect(screen.queryByText(/conv2/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Resolved' }));
    expect(screen.queryByText(/conv1/)).not.toBeInTheDocument();
    expect(screen.getByText(/conv2/)).toBeInTheDocument();
  });

  it('renders claimed items as table rows', () => {
    issuesData = [
      { id: 'e1', conversationId: 'conv1', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Claimed', markdown: 'md1', claimedByUserId: 'l3', claimedAt: new Date().toISOString() },
      { id: 'e2', conversationId: 'conv2', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Claimed', markdown: 'md2', claimedByUserId: 'l3', claimedAt: new Date().toISOString() },
      { id: 'e3', conversationId: 'conv3', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Claimed', markdown: 'md3', claimedByUserId: 'l3', claimedAt: new Date().toISOString() },
    ];

    render(<MyIssuesPage />);

    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(4); // 1 header row + 3 data rows
  });

  it('opening an issue card shows its cached Markdown', () => {
    issuesData = [
      { id: 'e1', conversationId: 'conv1', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Claimed', markdown: '## Detail\n\nsome markdown body', claimedByUserId: 'l3', claimedAt: new Date().toISOString() },
    ];

    render(<MyIssuesPage />);

    expect(screen.queryByText('Detail')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText(/conv1/));
    expect(screen.getByText('Detail')).toBeInTheDocument();
  });

  it('shows an empty state with no issues', () => {
    issuesData = [];
    render(<MyIssuesPage />);

    expect(screen.getByText('No issues here.')).toBeInTheDocument();
  });
});
