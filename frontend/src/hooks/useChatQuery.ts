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
}

export function useChatQuery() {
  return useMutation({
    mutationFn: async (request: ChatQueryRequest) => {
      const { data } = await apiClient.post<ChatQueryResponse>('/chat/query', request);
      return data;
    },
  });
}
