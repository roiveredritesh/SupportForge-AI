import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface FeedbackSummary {
  useful: number;
  notUseful: number;
}

// U4: project-scoped useful/not-useful feedback counts, open to any project member -- distinct
// from useFeedbackDashboard, which reads the Admin-only org-scoped /feedback/dashboard endpoint.
export function useFeedbackSummary(projectId: string | null) {
  return useQuery({
    queryKey: ['feedback-summary', projectId],
    queryFn: async () => {
      const { data } = await apiClient.get<FeedbackSummary>(`/projects/${projectId}/feedback-summary`);
      return data;
    },
    enabled: !!projectId,
  });
}
