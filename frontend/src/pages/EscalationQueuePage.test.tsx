import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import EscalationQueuePage from './EscalationQueuePage';
import type { Escalation } from '../hooks/useEscalations';

const claimMock = vi.fn();
let queueData: Escalation[] = [];

vi.mock('../hooks/useEscalations', () => ({
  useEscalationQueue: () => ({ data: queueData, isLoading: false }),
  useClaimEscalation: () => ({ mutate: claimMock, isPending: false }),
}));

// U21: queue page lists open escalations; opening one shows the cached Markdown (already there,
// no re-query) directly.
describe('EscalationQueuePage', () => {
  it('lists open escalations and opens one to show its cached Markdown', () => {
    queueData = [
      {
        id: 'e1', conversationId: 'conv1', projectId: 'proj1', escalatedByUserId: 'l1-user',
        escalatedAt: new Date().toISOString(), status: 'Open',
        markdown: '## Escalated Conversation\n\n### Code Findings\n- backend/ChatController.cs:219',
      },
    ];

    render(<EscalationQueuePage />);

    expect(screen.getByText('conv1')).toBeInTheDocument();
    expect(screen.queryByText(/Code Findings/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('conv1'));

    expect(screen.getByText('Code Findings')).toBeInTheDocument();
    expect(screen.getByText(/backend\/ChatController\.cs/)).toBeInTheDocument();
  });

  it('renders open escalations as table rows', () => {
    queueData = [
      { id: 'e1', conversationId: 'conv1', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Open', markdown: 'md1' },
      { id: 'e2', conversationId: 'conv2', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Open', markdown: 'md2' },
      { id: 'e3', conversationId: 'conv3', projectId: 'proj1', escalatedByUserId: 'l1', escalatedAt: new Date().toISOString(), status: 'Open', markdown: 'md3' },
    ];

    render(<EscalationQueuePage />);

    const table = screen.getByRole('table');
    expect(table).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(4); // 1 header row + 3 data rows
    expect(screen.getByText('conv1')).toBeInTheDocument();
    expect(screen.getByText('conv2')).toBeInTheDocument();
    expect(screen.getByText('conv3')).toBeInTheDocument();
  });

  it('claiming an escalation calls the claim mutation', () => {
    queueData = [
      {
        id: 'e1', conversationId: 'conv1', projectId: 'proj1', escalatedByUserId: 'l1-user',
        escalatedAt: new Date().toISOString(), status: 'Open', markdown: 'md',
      },
    ];

    render(<EscalationQueuePage />);
    fireEvent.click(screen.getByText('Claim'));

    expect(claimMock).toHaveBeenCalledWith('e1');
  });

  it('shows an empty state with no open escalations', () => {
    queueData = [];
    render(<EscalationQueuePage />);

    expect(screen.getByText('No open escalations.')).toBeInTheDocument();
  });
});
