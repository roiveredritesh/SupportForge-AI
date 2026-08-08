import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface CreateOrgRequest {
  id: string;
  name: string;
  gitHubAccessToken?: string | null;
}

export function useCreateOrg() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: CreateOrgRequest) => {
      const { data } = await apiClient.post('/orgs', request);
      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['orgs'] }),
  });
}
