import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export function useTriggerIngestion() {
  return useMutation({
    mutationFn: async (projectId: string) => {
      await apiClient.post('/ingestion/trigger', { projectId });
    },
  });
}
