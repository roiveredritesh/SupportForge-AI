import { useEffect } from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import ChatPage from './ChatPage';
import { useAppStore } from '../store/useAppStore';
import { useAuthStore } from '../store/useAuthStore';

// U27: ChatPage's "Invite Engineer" button + user picker. ConversationSidebar is stubbed to
// immediately select "conv1" (via onSelect) so the button -- gated on having an active
// conversation as well as role -- has something to invite into, without needing a real
// conversation list round-trip.
vi.mock('../components/ConversationSidebar', () => ({
  ConversationSidebar: ({ onSelect }: { onSelect: (id: string) => void }) => {
    useEffect(() => onSelect('conv1'), [onSelect]);
    return null;
  },
}));

// U27's own hooks (useEmployees/useInviteToConversation/usePresence) are mocked below -- this
// apiClient stub only needs to satisfy ChatPage's other, pre-existing data hooks (useConversation,
// useProjects -- both via ProjectSwitcher and directly for orgId resolution) well enough that they
// don't throw when this test's assertions wait long enough for those queries to resolve. The
// project's orgId (not a separate useOrgs() call -- employees never get an OrgMembership row) is
// what ChatPage reads to resolve which org's employee list to fetch.
vi.mock('../lib/apiClient', () => ({
  apiClient: {
    get: vi.fn((url: string) => {
      if (url === '/projects') return Promise.resolve({ data: [{ id: 'proj1', name: 'Proj 1', orgId: 'org1' }] });
      if (url.startsWith('/conversations/')) return Promise.resolve({ data: { id: 'conv1', projectId: 'proj1', title: 't', messages: [] } });
      return Promise.resolve({ data: [] });
    }),
    post: vi.fn().mockResolvedValue({ data: {} }),
    defaults: { baseURL: 'http://localhost/api' },
  },
}));

vi.mock('../hooks/useChatQueryStream', () => ({
  useChatQueryStream: () => ({
    draft: '', confidence: undefined, conversationId: undefined, sources: [], totalTokensUsed: undefined,
    codeDetails: undefined, commitHistory: undefined, isStreaming: false, error: undefined,
    start: vi.fn(), retry: vi.fn(), reset: vi.fn(),
  }),
}));

vi.mock('../hooks/usePresence', () => ({ usePresence: () => ({ participantIds: [] }) }));

vi.mock('../hooks/useEmployees', () => ({
  useEmployees: () => ({ data: [{ id: 'eng1', userName: 'bob-engineer', role: 'L2', projectIds: ['proj1'] }] }),
}));

const inviteMutate = vi.fn();
vi.mock('../hooks/useInviteToConversation', () => ({
  useInviteToConversation: () => ({ mutate: inviteMutate, isPending: false }),
}));

function renderChatPage() {
  const client = new QueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ChatPage Invite Engineer', () => {
  beforeEach(() => {
    inviteMutate.mockClear();
    useAppStore.setState({ selectedProjectId: 'proj1' });
  });

  it('shows the Invite Engineer button for L2/L3/Admin sessions', async () => {
    useAuthStore.setState({ role: 'L3' });
    renderChatPage();

    await waitFor(() => expect(screen.getByText('Invite Engineer')).toBeInTheDocument());
  });

  it('hides the Invite Engineer button for L1 sessions', async () => {
    useAuthStore.setState({ role: 'L1' });
    renderChatPage();

    await waitFor(() => expect(screen.queryByText('Invite Engineer')).not.toBeInTheDocument());
  });

  it('invites the selected employee into the active conversation', async () => {
    useAuthStore.setState({ role: 'Admin' });
    renderChatPage();

    await waitFor(() => expect(screen.getByText('Invite Engineer')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Invite Engineer'));

    fireEvent.change(await screen.findByLabelText('Engineer to invite'), { target: { value: 'eng1' } });
    fireEvent.click(screen.getByText('Invite'));

    expect(inviteMutate).toHaveBeenCalledWith(
      { conversationId: 'conv1', projectId: 'proj1', userId: 'eng1' },
      expect.anything(),
    );
  });
});
