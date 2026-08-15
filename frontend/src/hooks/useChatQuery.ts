import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface ChatQueryRequest {
  projectId: string;
  query: string;
  screenshotBase64?: string;
  conversationId?: string;
}

export interface ChatSource {
  label: string;
  url: string;
}

export interface ChatQueryResponse {
  draft: string;
  confidence: number;
  conversationId: string;
  sources: ChatSource[];
  totalTokensUsed: number;
  // U6/U9: only present for L2/L3/Admin callers -- absent entirely for L1 (backend omits the
  // field, doesn't send it as null), so `undefined` here means "L1, or nothing found", not "L1".
  codeDetails?: string[];
}

export function useChatQuery() {
  return useMutation({
    mutationFn: async (request: ChatQueryRequest) => {
      const { data } = await apiClient.post<ChatQueryResponse>('/chat/query', request);
      return data;
    },
  });
}
