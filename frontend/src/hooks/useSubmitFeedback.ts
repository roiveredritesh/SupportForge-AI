import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

// U17: mirrors FeedbackReasonCode on the backend.
export type FeedbackReasonCode = 'Irrelevant' | 'WrongVersion' | 'Incomplete' | 'Other';

export interface FeedbackRequest {
  projectId: string;
  query: string;
  useful?: boolean;
  escalated: boolean;
  // U17: the sources shown alongside the rated answer -- drives KbSearchTool's per-source
  // down-weighting. Required (enforced client-side in the reason-code UI) when useful is false.
  sources?: string[];
  reasonCode?: FeedbackReasonCode;
}

export function useSubmitFeedback() {
  return useMutation({
    mutationFn: async (request: FeedbackRequest) => {
      await apiClient.post('/feedback', request);
    },
  });
}
