import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { backOfficeKeys, useBackOfficeApi } from './back-office-api';
import type { ChangeRoleRequest, ChangeStatusRequest, CreatePlatformUserRequest } from './types';

export function useBackOfficeUsers() {
  const api = useBackOfficeApi();

  return useQuery({
    queryKey: backOfficeKeys.users(),
    queryFn: () => api.list(),
  });
}

/** The four roles change when the code changes, not while somebody is on the screen. */
export function usePlatformRoles() {
  const api = useBackOfficeApi();

  return useQuery({
    queryKey: backOfficeKeys.roles(),
    queryFn: () => api.roles(),
    staleTime: 60 * 60 * 1000,
  });
}

/**
 * After any change — success, conflict or a lost connection — refetch the list.
 *
 * The server is the only trustworthy account of what happened. Patching the cache by hand would
 * show the change somebody *meant* to make, which after a timeout may not be the one that was
 * made.
 */
function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: backOfficeKeys.all });
}

export function useCreateBackOfficeUser() {
  const api = useBackOfficeApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: CreatePlatformUserRequest) => api.create(request),
    onSettled: invalidate,
  });
}

/**
 * Never retried automatically. Creating an account twice is refused by the email rule, but a
 * retried role change after a timeout can apply a grant somebody believes failed.
 */
export function useChangeRole(userId: string) {
  const api = useBackOfficeApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: ChangeRoleRequest) => api.changeRole(userId, request),
    onSettled: invalidate,
  });
}

export function useChangeStatus(userId: string) {
  const api = useBackOfficeApi();
  const invalidate = useInvalidate();

  return useMutation({
    mutationFn: (request: ChangeStatusRequest) => api.changeStatus(userId, request),
    onSettled: invalidate,
  });
}
