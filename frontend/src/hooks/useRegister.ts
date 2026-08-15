import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import { useAuthStore } from '../store/useAuthStore';

interface RegisterRequest {
  userName: string;
  password: string;
}

interface TokenResponse {
  accessToken: string;
  expiresAt: string;
}

// Self-service org+Admin signup -- POST /api/auth/register (AuthController.Register) creates a new
// user, a new Org, and makes the user that org's Admin, same response shape as useLogin's token
// endpoint. Mirrors useLogin.ts's shape exactly.
export function useRegister() {
  const setToken = useAuthStore((s) => s.setToken);
  return useMutation({
    mutationFn: async (request: RegisterRequest) => {
      const { data } = await apiClient.post<TokenResponse>('/auth/register', request);
      return data;
    },
    onSuccess: (data) => setToken(data.accessToken, data.expiresAt),
  });
}
