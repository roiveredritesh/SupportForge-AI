import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface CreateProjectRequest {
  id: string;
  name: string;
  repos: { owner: string; repo: string; defaultBranch: string }[];
  kbSources: { type: 'Documents'; location: string }[];
}

export function useCreateProject() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: CreateProjectRequest) => {
      const { data } = await apiClient.post('/projects', request);
      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
  });
}
