import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface ProjectRepo {
  owner: string;
  repo: string;
  defaultBranch: string;
}

export interface ProjectKbSource {
  type: string;
  location: string;
}

export interface Project {
  id: string;
  name: string;
  repos: ProjectRepo[];
  kbSources: ProjectKbSource[];
}

export function useProjects() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: async () => {
      const { data } = await apiClient.get<Project[]>('/projects');
      return data;
    },
  });
}
