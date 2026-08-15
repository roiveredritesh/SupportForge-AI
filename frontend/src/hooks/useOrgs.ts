import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface Org {
  id: string;
  name: string;
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
