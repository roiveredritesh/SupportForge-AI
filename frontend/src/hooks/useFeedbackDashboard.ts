import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface FeedbackDashboardEntry {
  projectId: string;
  query: string;
  createdAt: string;
  reasonCode?: string | null;
}

export interface FeedbackDashboardResponse {
  recentNegative: FeedbackDashboardEntry[];
  reasonCodeBreakdown: Record<string, number>;
}

// U18: Admin-only, scoped server-side to the Admin's own org/projects.
export function useFeedbackDashboard() {
  return useQuery({
    queryKey: ['feedback', 'dashboard'],
    queryFn: async () => {
      const { data } = await apiClient.get<FeedbackDashboardResponse>('/feedback/dashboard');
      return data;
    },
  });
}
