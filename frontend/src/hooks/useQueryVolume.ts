import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface QueryVolumePoint {
  date: string;
  count: number;
}

// U4: chat query volume by day (last 30 days) for the Dashboard's line chart.
export function useQueryVolume(projectId: string | null) {
  return useQuery({
    queryKey: ['query-volume', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<QueryVolumePoint[]>(`/projects/${projectId}/query-volume`);
      return data;
    },
    enabled: !!projectId,
  });
}
