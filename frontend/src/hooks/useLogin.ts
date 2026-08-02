import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

interface TokenRequest {
  userName: string;
  password: string;
}

interface TokenResponse {
  accessToken: string;
  expiresAt: string;
}

export function useLogin() {
  const setToken = useAuthStore((s) => s.setToken);
  return useMutation({
    mutationFn: async (request: TokenRequest) => {
      const { data } = await apiClient.post<TokenResponse>('/auth/token', request);
      return data;
    },
    onSuccess: (data) => setToken(data.accessToken, data.expiresAt),
  });
}
