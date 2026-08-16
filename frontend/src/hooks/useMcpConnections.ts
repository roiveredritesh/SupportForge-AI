import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';
import type { EmployeeRole } from './useEmployees';

export interface McpToolCatalogEntry {
  name: string;
  description: string;
  minRole: EmployeeRole;
}

export interface McpServerCatalogEntry {
  serverType: string;
  tools: McpToolCatalogEntry[];
}

export interface McpConnection {
  serverType: string;
  enabledTools: string[];
  // U7: null when GitHub reported no expiration header (e.g. fine-grained PATs) or the connect-time
  // probe failed -- see McpConnectionsController.Connect.
  expiresAt: string | null;
}

export interface ConnectMcpServerRequest {
  orgId: string;
  serverType: string;
  credential: string;
  enabledTools: string[];
}

// U16: supported MCP server types + their tools (GitHub only, this sprint). GetCatalog's route is
// nested under the org-scoped controller (same [Authorize(Roles="Admin")] gate as the rest of it)
// even though its content isn't org-specific, so this still takes an orgId to build the URL.
export function useMcpCatalog(orgId: string | undefined) {
  return useQuery({
    queryKey: ['mcp-catalog', orgId],
    enabled: !!orgId,
    queryFn: async () => {
      const { data } = await apiClient.get<McpServerCatalogEntry[]>(`/orgs/${orgId}/mcp-connections/catalog`);
      return data;
    },
  });
}

// Mirrors useOrgs.ts/useEmployees.ts's org-scoped query shape.
export function useMcpConnections(orgId: string | undefined) {
  return useQuery({
    queryKey: ['mcp-connections', orgId],
    enabled: !!orgId,
    queryFn: async () => {
      const { data } = await apiClient.get<McpConnection[]>(`/orgs/${orgId}/mcp-connections`);
      return data;
    },
  });
}

// Mirrors useRegisterEmployee.ts's useMutation + apiClient.post + invalidateQueries shape.
export function useConnectMcpServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: ConnectMcpServerRequest) => {
      const { orgId, ...body } = request;
      const { data } = await apiClient.post(`/orgs/${orgId}/mcp-connections`, body);
      return data;
    },
    onSuccess: (_data, request) => queryClient.invalidateQueries({ queryKey: ['mcp-connections', request.orgId] }),
  });
}

export function useDisconnectMcpServer() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ orgId, serverType }: { orgId: string; serverType: string }) => {
      await apiClient.delete(`/orgs/${orgId}/mcp-connections/${serverType}`);
    },
    onSuccess: (_data, { orgId }) => queryClient.invalidateQueries({ queryKey: ['mcp-connections', orgId] }),
  });
}
