import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface EscalationStats {
  open: number;
  claimed: number;
  resolved: number;
}

// U4: open/claimed/resolved escalation counts for the project.
export function useEscalationStats(projectId: string | null) {
  return useQuery({
    queryKey: ['escalation-stats', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<EscalationStats>(`/projects/${projectId}/escalation-stats`);
      return data;
    },
    enabled: !!projectId,
  });
}
