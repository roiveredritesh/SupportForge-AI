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
  // Website sources only: also index same-host pages linked directly from the root page.
  // Omitted/false matches prior behavior (single page only).
  crawlLinkedPages?: boolean;
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
