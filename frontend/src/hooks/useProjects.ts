import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export type KbSourceType = 'Documents' | 'Confluence' | 'Website';

export interface ProjectRepo {
  owner: string;
  repo: string;
  defaultBranch: string;
  accessTokenSecretName?: string | null;
  lastSyncedAt?: string | null;
}

export interface ProjectKbSource {
  type: KbSourceType;
  location: string;
  repoOwner?: string | null;
  repoName?: string | null;
  lastSyncedAt?: string | null;
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
