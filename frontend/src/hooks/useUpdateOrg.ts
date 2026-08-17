import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import type { Org } from './useOrgs';

// POST /api/orgs does a raw upsert of the whole posted body (OrgsController.CreateOrUpdate), so a
// caller must send the complete Org object -- a partial patch would blank out required fields
// (name/contactPerson/contactNumber/industry) on save.
export function useUpdateOrg() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (org: Org) => {
      const { data } = await apiClient.post<Org>('/orgs', org);
      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['orgs'] }),
  });
}
