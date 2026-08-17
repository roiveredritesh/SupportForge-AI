import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import type { ProjectKbSource, ProjectRepo } from './useProjects';

export interface CreateProjectRequest {
  id: string;
  name: string;
  repos: ProjectRepo[];
  kbSources: ProjectKbSource[];
  scheduledSyncIntervalHours?: number | null;
  codeClassificationEnabled?: boolean;
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
