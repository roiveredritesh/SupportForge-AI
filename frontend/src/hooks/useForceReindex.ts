import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export function useForceReindex() {
  return useMutation({
    mutationFn: async (projectId: string) => {
      await apiClient.post('/ingestion/force-reindex', { projectId });
    },
  });
}
