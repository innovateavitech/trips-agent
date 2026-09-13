/**
 * The shapes the back-office user screens read — mirrors of the records in
 * backend/services/TripsAgent.Contracts/Platform/PlatformUserContracts.cs.
 *
 * Hand-written, with the same debt the agency screens' types carry: when the console moves onto
 * the generated `@trips/api-client`, delete this file and import from there.
 */

/** UserStatus in the domain, as the API sends it. */
export type PlatformUserStatus = 'Invited' | 'Active' | 'Suspended' | 'Deactivated';

/** PlatformUserResponse — one Trips back-office account. */
export interface PlatformUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  status: PlatformUserStatus;
  /** The platform roles held. Usually one; the model allows more. */
  roles: string[];
  /** Every permission those roles carry, flattened. What the token will contain. */
  permissions: string[];
  lastLoginAt: string | null;
  createdAt: string;
}

/** PlatformRoleResponse — one of the four back-office roles. */
export interface PlatformRole {
  id: string;
  name: string;
  description: string;
  permissions: string[];
}

/** No password: the account is created invited and the person sets their own. */
export interface CreatePlatformUserRequest {
  email: string;
  firstName: string;
  lastName: string;
  roleName: string;
  reason: string;
}

export interface ChangeRoleRequest {
  roleName: string;
  reason: string;
}

export interface ChangeStatusRequest {
  status: PlatformUserStatus;
  reason: string;
}
