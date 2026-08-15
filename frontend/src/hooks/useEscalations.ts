import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface Escalation {
  id: string;
  conversationId: string;
  projectId: string;
  escalatedByUserId: string;
  escalatedAt: string;
  markdown: string;
  status: 'Open' | 'Claimed' | 'Resolved';
  claimedByUserId?: string | null;
  claimedAt?: string | null;
  resolvedAt?: string | null;
}

// U21: open escalations across the caller's projects (L2/L3/Admin only server-side).
export function useEscalationQueue() {
  return useQuery({
    queryKey: ['escalations', 'queue'],
    queryFn: async () => {
      const { data } = await apiClient.get<Escalation[]>('/escalations');
      return data;
    },
  });
}

export function useClaimEscalation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const { data } = await apiClient.post<Escalation>(`/escalations/${id}/claim`);
      return data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['escalations', 'queue'] });
      void queryClient.invalidateQueries({ queryKey: ['escalations', 'my-issues'] });
    },
  });
}

// U21: the calling engineer's claimed-open + recently-resolved escalations.
export function useMyIssues() {
  return useQuery({
    queryKey: ['escalations', 'my-issues'],
    queryFn: async () => {
      const { data } = await apiClient.get<Escalation[]>('/engineers/me/issues');
      return data;
    },
  });
}
