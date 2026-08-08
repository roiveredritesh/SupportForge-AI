import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface Org {
  id: string;
  name: string;
  gitHubAccessToken?: string | null;
}

export function useOrgs() {
  return useQuery({
    queryKey: ['orgs'],
    queryFn: async () => {
      const { data } = await apiClient.get<Org[]>('/orgs');
      return data;
    },
  });
}
