import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface TokenUsageSummary {
  total: number;
  bySource: Record<string, number>;
}

// Reads ProjectsController's existing GET {id}/token-usage (chat vs. ingestion breakdown).
export function useTokenUsage(projectId: string | null) {
  return useQuery({
    queryKey: ['token-usage', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<TokenUsageSummary>(`/projects/${projectId}/token-usage`);
      return data;
    },
    enabled: !!projectId,
  });
}
