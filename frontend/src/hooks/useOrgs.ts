import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface Org {
  id: string;
  name: string;
  contactPerson?: string;
  contactNumber?: string;
  industry?: string;
  address?: string | null;
  // Org-wide master switch for the code-graph Tier 2 LLM classification stage -- a project's own
  // codeClassificationEnabled (useProjects.ts) also has to be true; neither flag alone enables it.
  codeClassificationEnabled?: boolean;
}

// U8: one org per Admin (self-service registration, U4) -- AdminPage's Employees section uses
// the first (only) org this caller belongs to as the employee-registration target.
export function useOrgs() {
  return useQuery({
    queryKey: ['orgs'],
    queryFn: async () => {
      const { data } = await apiClient.get<Org[]>('/orgs');
      return data;
    },
  });
}
