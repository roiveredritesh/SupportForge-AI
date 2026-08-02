import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface FreshnessScore {
  isFresh: boolean;
  staleSources: string[];
}

export function useFreshness(projectId: string) {
  return useQuery({
    queryKey: ['freshness', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<FreshnessScore>(`/projects/${projectId}/freshness`);
      return data;
    },
  });
}
