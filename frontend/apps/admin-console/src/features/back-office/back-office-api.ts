import { createContext, useContext } from 'react';
import type { ApiClient } from '../../lib/api/client';
import type {
  ChangeRoleRequest,
  ChangeStatusRequest,
  CreatePlatformUserRequest,
  PlatformRole,
  PlatformUser,
} from './types';

/**
 * Trips' own accounts, and the role each of them holds.
 *
 * Every call is behind `platform.user.manage`, which only a Super Admin holds: this is the screen
 * that decides who else can suspend a customer, so it is the one that most needs a short list of
 * people who can reach it.
 */
export interface BackOfficeApi {
  /** GET /api/v1/admin/users — every back-office account. */
  list(): Promise<PlatformUser[]>;

  /** GET /api/v1/admin/users/roles — the four roles and what each of them carries. */
  roles(): Promise<PlatformRole[]>;

  /** POST /api/v1/admin/users — creates one, invited, with one role. 409 if the email is taken. */
  create(request: CreatePlatformUserRequest): Promise<PlatformUser>;

  /** POST …/{id}/role — replaces the role held. Always carries a reason. */
  changeRole(userId: string, request: ChangeRoleRequest): Promise<PlatformUser>;

  /** POST …/{id}/status — suspends or reactivates. Refused on your own account. */
  changeStatus(userId: string, request: ChangeStatusRequest): Promise<PlatformUser>;
}

const BASE = '/api/v1/admin/users';

export function createHttpBackOfficeApi(client: ApiClient): BackOfficeApi {
  const user = (id: string) => `${BASE}/${encodeURIComponent(id)}`;

  return {
    list: () => client.get<PlatformUser[]>(BASE),
    roles: () => client.get<PlatformRole[]>(`${BASE}/roles`),
    create: (request) => client.post<PlatformUser>(BASE, request),
    changeRole: (id, request) => client.post<PlatformUser>(`${user(id)}/role`, request),
    changeStatus: (id, request) => client.post<PlatformUser>(`${user(id)}/status`, request),
  };
}

const BackOfficeApiContext = createContext<BackOfficeApi | null>(null);

export const BackOfficeApiProvider = BackOfficeApiContext.Provider;

export function useBackOfficeApi(): BackOfficeApi {
  const api = useContext(BackOfficeApiContext);
  if (!api) throw new Error('useBackOfficeApi must be used inside a <BackOfficeApiProvider>.');
  return api;
}

export const backOfficeKeys = {
  all: ['back-office'] as const,
  users: () => [...backOfficeKeys.all, 'users'] as const,
  roles: () => [...backOfficeKeys.all, 'roles'] as const,
};
