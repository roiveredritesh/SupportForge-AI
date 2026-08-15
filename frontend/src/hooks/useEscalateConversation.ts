import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface EscalateResponse {
  escalationId: string;
  status: 'Open' | 'Claimed' | 'Resolved';
}

// U19/U20: available to every role including L1 -- the endpoint never returns the cached Markdown
// (only an id/status), so there's nothing here for an L1 caller to leak even if this response were
// rendered directly.
export function useEscalateConversation() {
  return useMutation({
    mutationFn: async (conversationId: string) => {
      const { data } = await apiClient.post<EscalateResponse>(`/conversations/${conversationId}/escalate`);
      return data;
    },
  });
}
