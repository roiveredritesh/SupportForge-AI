import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import type { QueryVolumePoint } from './useQueryVolume';

export interface OrgTokenUsageEntry {
  projectId: string;
  userId: string | null;
  totalTokens: number;
  createdAt: string;
  source: string;
}

export interface OrgTokenUsage {
  chart: QueryVolumePoint[];
  entries: OrgTokenUsageEntry[];
  projectIds: string[];
}

export interface OrgTokenUsageFilters {
  from?: string;
  to?: string;
  projectId?: string;
  userId?: string;
}

// U8: org-wide token usage for the Admin-only Token Usage page -- backed by
// OrgsController.GetOrgTokenUsage (U6), not ProjectsController's per-project endpoint.
export function useOrgTokenUsage(orgId: string | undefined, filters: OrgTokenUsageFilters) {
  return useQuery({
    queryKey: ['org-token-usage', orgId, filters],
    queryFn: async () => {
      const { data } = await apiClient.get<OrgTokenUsage>(`/orgs/${orgId}/token-usage`, { params: filters });
      return data;
    },
    enabled: !!orgId,
  });
}
