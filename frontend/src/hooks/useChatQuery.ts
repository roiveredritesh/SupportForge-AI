import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface ChatQueryRequest {
  projectId: string;
  query: string;
  screenshotBase64?: string;
  conversationId?: string;
  // U10: free-text product version + optional key-value config, biasing KB retrieval and driving
  // the drafted answer's version disclaimer.
  productVersion?: string;
  config?: Record<string, string>;
}

export interface ChatSource {
  label: string;
  url: string;
}

// U11: one recent commit touching a matched code file, with a best-effort PR link.
export interface CommitInfo {
  sha: string;
  author: string;
  date: string;
  message: string;
  prUrl?: string;
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
  // U11: same L2/L3/Admin-only gating as codeDetails above.
  commitHistory?: CommitInfo[];
}

export function useChatQuery() {
  return useMutation({
    mutationFn: async (request: ChatQueryRequest) => {
      const { data } = await apiClient.post<ChatQueryResponse>('/chat/query', request);
      return data;
    },
  });
}
