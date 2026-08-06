import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface ConversationSummary {
  id: string;
  projectId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
}

export interface ChatSource {
  label: string;
  url: string;
}

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  confidence?: number;
  sources?: ChatSource[];
  createdAt: string;
}

export interface ConversationDetail extends ConversationSummary {
  messages: ChatMessage[];
}

export function useConversations(projectId: string | null) {
  return useQuery({
    queryKey: ['conversations', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<ConversationSummary[]>('/conversations', { params: { projectId } });
      return data;
    },
    enabled: !!projectId,
  });
}

export function useConversation(id: string | null) {
  return useQuery({
    queryKey: ['conversation', id],
    queryFn: async () => {
      const { data } = await apiClient.get<ConversationDetail>(`/conversations/${id}`);
      return data;
    },
    enabled: !!id,
  });
}

export function useCreateConversation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (projectId: string) => {
      const { data } = await apiClient.post<ConversationSummary>('/conversations', { projectId });
      return data;
    },
    onSuccess: (conversation) => queryClient.invalidateQueries({ queryKey: ['conversations', conversation.projectId] }),
  });
}

export function useDeleteConversation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/conversations/${id}`);
      return id;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['conversations'] }),
  });
}
