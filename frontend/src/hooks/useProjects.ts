import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export type KbSourceType = 'Documents' | 'Confluence' | 'Website';

export interface ProjectRepo {
  owner: string;
  repo: string;
  defaultBranch: string;
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
  // Sprint 0 (U2): the org this project belongs to. Employees (L1/L2/L3) never get an OrgMembership
  // row -- only the Admin who created the org does -- so a project's own OrgId is the only way for
  // an employee-role caller to resolve "their" org (e.g. ChatPage's Invite Engineer picker).
  orgId?: string | null;
  repos: ProjectRepo[];
  kbSources: ProjectKbSource[];
  // Overrides the server's global scheduled-sync cadence for this project's KB sources.
  // Null/omitted falls back to the server default (Freshness:ScheduledSyncIntervalHours).
  scheduledSyncIntervalHours?: number | null;
  // Per-project opt-in for the code-graph Tier 2 LLM classification stage -- also requires the
  // org-wide toggle (useOrgs.ts's Org.codeClassificationEnabled) to be on; neither alone enables it.
  codeClassificationEnabled?: boolean;
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
