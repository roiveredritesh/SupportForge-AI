import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import type { ConversationSummary } from './useConversations';

export interface InviteToConversationRequest {
  conversationId: string;
  projectId: string;
  userId: string;
}

// U27: POST /api/conversations/{id}/invite -- server gates this to L2/L3/Admin (see
// ConversationsController.Invite); ChatPage only renders the "Invite Engineer" button for those
// roles (RequireRole is a UI convenience, not the real gate).
export function useInviteToConversation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ conversationId, userId }: InviteToConversationRequest) => {
      const { data } = await apiClient.post<ConversationSummary>(`/conversations/${conversationId}/invite`, { userId });
      return data;
    },
    onSuccess: (_data, request) => {
      void queryClient.invalidateQueries({ queryKey: ['conversations', request.projectId] });
      void queryClient.invalidateQueries({ queryKey: ['conversation', request.conversationId] });
    },
  });
}
