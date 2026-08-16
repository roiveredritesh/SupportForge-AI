import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export function useChangePassword() {
  return useMutation({
    mutationFn: async (request: ChangePasswordRequest) => {
      await apiClient.post('/auth/change-password', request);
    },
  });
}
