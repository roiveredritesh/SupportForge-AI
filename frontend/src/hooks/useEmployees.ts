import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export type EmployeeRole = 'L1' | 'L2' | 'L3' | 'Admin';

export interface Employee {
  id: string;
  userName: string;
  role: EmployeeRole;
  projectIds: string[];
}

export function useEmployees(orgId: string | undefined) {
  return useQuery({
    queryKey: ['employees', orgId],
    enabled: !!orgId,
    queryFn: async () => {
      const { data } = await apiClient.get<Employee[]>(`/orgs/${orgId}/employees`);
      return data;
    },
  });
}
