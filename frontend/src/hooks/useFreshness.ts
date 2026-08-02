import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface SourceFreshness {
  name: string;
  lastSyncedAt: string | null;
  isStale: boolean;
}

export interface FreshnessScore {
  isFresh: boolean;
  staleSources: string[];
  sources: SourceFreshness[];
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
