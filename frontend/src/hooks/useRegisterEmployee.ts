import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import type { EmployeeRole } from './useEmployees';

export interface RegisterEmployeeRequest {
  orgId: string;
  userName: string;
  password: string;
  role: EmployeeRole;
  projectIds: string[];
}

// Mirrors useCreateProject.ts's useMutation + apiClient.post + invalidateQueries shape.
export function useRegisterEmployee() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: RegisterEmployeeRequest) => {
      const { orgId, ...body } = request;
      const { data } = await apiClient.post(`/orgs/${orgId}/employees`, body);
      return data;
    },
    onSuccess: (_data, request) => queryClient.invalidateQueries({ queryKey: ['employees', request.orgId] }),
  });
}
