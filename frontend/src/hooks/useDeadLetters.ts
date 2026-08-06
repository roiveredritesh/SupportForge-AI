import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface DeadLetterEntry {
  id: string;
  projectId: string;
  jobType: string;
  error: string;
  failedAt: string;
}

export function useDeadLetters(projectId: string) {
  return useQuery({
    queryKey: ['dead-letters', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<DeadLetterEntry[]>('/ingestion/dead-letters', { params: { projectId } });
      return data;
    },
  });
}

export function useDismissDeadLetter(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/ingestion/dead-letters/${id}`);
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['dead-letters', projectId] }),
  });
}
