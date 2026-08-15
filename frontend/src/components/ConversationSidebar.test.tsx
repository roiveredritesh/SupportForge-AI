import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ConversationSidebar } from './ConversationSidebar';

const mutate = vi.fn();
vi.mock('../hooks/useConversations', () => ({
  useConversations: () => ({
    data: [
      { id: 'conv1', projectId: 'proj1', title: 'Plain chat', createdAt: '2026-01-01', updatedAt: '2026-01-01', invitedUserIds: [] },
      { id: 'conv2', projectId: 'proj1', title: 'Collab chat', createdAt: '2026-01-01', updatedAt: '2026-01-01', invitedUserIds: ['eng1'] },
    ],
  }),
  useDeleteConversation: () => ({ mutate }),
}));

describe('ConversationSidebar', () => {
  // U27: marks a conversation with an active collaborative session (a non-empty InvitedUserIds).
  it('marks only the conversation with invited participants', () => {
    render(<ConversationSidebar projectId="proj1" activeConversationId={null} onSelect={vi.fn()} onNewChat={vi.fn()} />);

    const plainRow = screen.getByText('Plain chat').closest('li')!;
    const collabRow = screen.getByText('Collab chat').closest('li')!;

    expect(plainRow.querySelector('[aria-label="Collaborative session active"]')).toBeNull();
    expect(collabRow.querySelector('[aria-label="Collaborative session active"]')).not.toBeNull();
  });
});
