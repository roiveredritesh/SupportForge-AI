import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface Project {
  id: string;
  name: string;
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
