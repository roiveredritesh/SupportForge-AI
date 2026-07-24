import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface FeedbackRequest {
  projectId: string;
  query: string;
  useful?: boolean;
  escalated: boolean;
}

export function useSubmitFeedback() {
  return useMutation({
    mutationFn: async (request: FeedbackRequest) => {
      await apiClient.post('/feedback', request);
    },
  });
}
