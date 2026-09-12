import type { SubAgentsApi } from '../subagents-api';
import type {
  Allowance,
  NetworkPerformance,
  SubAgent,
  SubAgentPermission,
  SubAgentScope,
} from '../types';

/**
 * A small network in memory, for demo mode (`VITE_AUTH_MODE=mock`) and for the
 * screens' own tests. It keeps its state between calls so freezing a sub-agent
 * or moving an allowance behaves the way the real one does.
 */
const PERMISSIONS: ReadonlyArray<Omit<SubAgentPermission, 'isDenied' | 'reason'>> = [
  { code: 'booking.search', category: 'Bookings', description: 'Search flights and buses' },
  { code: 'booking.create', category: 'Bookings', description: 'Create a booking and hold it' },
  {
    code: 'booking.issue',
    category: 'Bookings',
    description: 'Issue tickets — this spends real money',
  },
  { code: 'wallet.view', category: 'Money', description: 'See the wallet balance and its ledger' },
  { code: 'margin.view', category: 'Money', description: 'See net rates and the markup applied' },
  {
    code: 'catalog.view',
    category: 'Catalog',
    description: 'See tours, visas and group departures',
  },
  { code: 'report.view', category: 'Reports', description: "See this agency's reports" },
];

export function createMockSubAgentsApi(): SubAgentsApi {
  const subAgents: SubAgent[] = [
    {
      id: 'sub-ikeja',
      legalName: 'Ikeja Branch Limited',
      tradingName: 'Ikeja Travel',
      slug: 'ikeja-branch',
      status: 'Verified',
      statusReason: null,
      createdAt: '2026-07-02T09:00:00Z',
      hasOpenInvitation: false,
      scopeCount: 2,
      deniedPermissionCount: 1,
      canSeeMargin: false,
      allowanceSpentMinor: 640_000,
      allowanceLimitMinor: 1_000_000,
      allowanceCurrency: 'NGN',
    },
    {
      id: 'sub-yaba',
      legalName: 'Yaba Travel Limited',
      tradingName: null,
      slug: 'yaba-travel',
      status: 'PendingVerification',
      statusReason: null,
      createdAt: '2026-09-01T11:30:00Z',
      hasOpenInvitation: true,
      scopeCount: 0,
      deniedPermissionCount: 0,
      canSeeMargin: true,
      allowanceSpentMinor: null,
      allowanceLimitMinor: null,
      allowanceCurrency: null,
    },
  ];

  const scopes = new Map<string, SubAgentScope[]>([
    [
      'sub-ikeja',
      [
        { id: 'scope-1', productType: 'Flight', supplierId: null, supplierName: null },
        { id: 'scope-2', productType: 'Bus', supplierId: 'sup-1', supplierName: 'Trips Africa' },
      ],
    ],
    ['sub-yaba', []],
  ]);

  const denied = new Map<string, Map<string, string>>([
    ['sub-ikeja', new Map([['margin.view', 'Branches do not see what we pay.']])],
    ['sub-yaba', new Map()],
  ]);

  const allowances = new Map<string, Allowance | null>([
    [
      'sub-ikeja',
      {
        subAgencyId: 'sub-ikeja',
        currency: 'NGN',
        spentMinor: 640_000,
        limitMinor: 1_000_000,
        remainingMinor: 360_000,
        period: 'Monthly',
        status: 'Active',
        resetsAt: '2026-10-01T00:00:00Z',
      },
    ],
    ['sub-yaba', null],
  ]);

  const find = (id: string) => subAgents.find((candidate) => candidate.id === id);

  return {
    listSubAgents: () =>
      Promise.resolve({
        subAgents: [...subAgents],
        maxSubAgents: 5,
        canAddAnother: subAgents.length < 5,
        cannotAddReason: subAgents.length < 5 ? null : 'Your plan allows 5 sub-agents.',
      }),

    invite: (request) => {
      const id = `sub-${subAgents.length + 1}`;

      subAgents.push({
        id,
        legalName: request.legalName,
        tradingName: request.tradingName,
        slug: request.legalName.toLowerCase().replaceAll(/[^a-z0-9]+/g, '-'),
        status: 'PendingVerification',
        statusReason: null,
        createdAt: new Date().toISOString(),
        hasOpenInvitation: true,
        scopeCount: 0,
        deniedPermissionCount: 0,
        canSeeMargin: true,
        allowanceSpentMinor: null,
        allowanceLimitMinor: null,
        allowanceCurrency: null,
      });

      scopes.set(id, []);
      denied.set(id, new Map());
      allowances.set(id, null);

      return Promise.resolve({
        id,
        email: request.email,
        // Short and obviously fake on purpose: a realistic-looking one here would
        // be flagged by the pre-commit secret scan, and rightly so.
        invitationToken: 'demo-link',
        expiresAt: new Date(Date.now() + 7 * 24 * 3600_000).toISOString(),
      });
    },

    freeze: (id, reason) => {
      const subAgent = find(id);
      if (subAgent) {
        subAgent.status = 'Suspended';
        subAgent.statusReason = reason;
      }
      return Promise.resolve();
    },

    unfreeze: (id, reason) => {
      const subAgent = find(id);
      if (subAgent) {
        subAgent.status = 'Verified';
        subAgent.statusReason = reason;
      }
      return Promise.resolve();
    },

    revoke: (id, reason) => {
      const subAgent = find(id);
      if (subAgent) {
        subAgent.status = 'Terminated';
        subAgent.statusReason = reason;
        subAgent.allowanceLimitMinor = 0;
      }
      return Promise.resolve();
    },

    listScopes: (id) => Promise.resolve([...(scopes.get(id) ?? [])]),

    grantScope: (id, productType, supplierId) => {
      const existing = scopes.get(id) ?? [];

      if (
        !existing.some(
          (scope) => scope.productType === productType && scope.supplierId === supplierId,
        )
      ) {
        existing.push({
          id: `scope-${existing.length + 1}-${productType}`,
          productType,
          supplierId,
          supplierName: supplierId === null ? null : 'Trips Africa',
        });
      }

      scopes.set(id, existing);
      syncScopeCount(id);

      return Promise.resolve();
    },

    revokeScope: (id, scopeId) => {
      scopes.set(
        id,
        (scopes.get(id) ?? []).filter((scope) => scope.id !== scopeId),
      );
      syncScopeCount(id);
      return Promise.resolve();
    },

    listPermissions: (id) => {
      const blocked = denied.get(id) ?? new Map<string, string>();

      return Promise.resolve(
        PERMISSIONS.map((permission) => ({
          ...permission,
          isDenied: blocked.has(permission.code),
          reason: blocked.get(permission.code) ?? null,
        })),
      );
    },

    denyPermission: (id, code, reason) => {
      (denied.get(id) ?? new Map<string, string>()).set(code, reason);
      denied.set(id, denied.get(id) ?? new Map([[code, reason]]));
      syncPermissionCount(id);
      return Promise.resolve();
    },

    allowPermission: (id, code) => {
      denied.get(id)?.delete(code);
      syncPermissionCount(id);
      return Promise.resolve();
    },

    getAllowance: (id) => Promise.resolve(allowances.get(id) ?? null),

    setAllowance: (id, limitMinor, period) => {
      const existing = allowances.get(id);
      const spentMinor = existing?.spentMinor ?? 0;

      const allowance: Allowance = {
        subAgencyId: id,
        currency: 'NGN',
        spentMinor,
        limitMinor,
        remainingMinor: Math.max(limitMinor - spentMinor, 0),
        period,
        status: existing?.status ?? 'Active',
        resetsAt: period === 'Lifetime' ? null : '2026-10-01T00:00:00Z',
      };

      allowances.set(id, allowance);
      syncAllowance(id, allowance);

      return Promise.resolve(allowance);
    },

    freezeAllowance: (id) => setStatus(id, 'Frozen'),

    unfreezeAllowance: (id) => setStatus(id, 'Active'),

    networkPerformance: (from, to) =>
      Promise.resolve<NetworkPerformance>({
        from,
        to,
        currency: 'NGN',
        orders: 48,
        salesMinor: 18_400_000,
        marginMinor: 1_620_000,
        members: [
          {
            agencyId: 'principal',
            name: 'Lagos Travel',
            status: 'Verified',
            isPrincipal: true,
            orders: 31,
            salesMinor: 12_900_000,
            marginMinor: 1_180_000,
            allowanceSpentMinor: null,
            allowanceLimitMinor: null,
          },
          {
            agencyId: 'sub-ikeja',
            name: 'Ikeja Travel',
            status: 'Verified',
            isPrincipal: false,
            orders: 17,
            salesMinor: 5_500_000,
            marginMinor: 440_000,
            allowanceSpentMinor: 640_000,
            allowanceLimitMinor: 1_000_000,
          },
        ],
      }),
  };

  function setStatus(id: string, status: Allowance['status']) {
    const existing = allowances.get(id);
    if (!existing) return Promise.reject(new Error('That sub-agent has no allowance.'));

    const allowance = { ...existing, status };
    allowances.set(id, allowance);

    return Promise.resolve(allowance);
  }

  function syncScopeCount(id: string) {
    const subAgent = find(id);
    if (subAgent) subAgent.scopeCount = (scopes.get(id) ?? []).length;
  }

  function syncPermissionCount(id: string) {
    const subAgent = find(id);
    const blocked = denied.get(id) ?? new Map<string, string>();

    if (subAgent) {
      subAgent.deniedPermissionCount = blocked.size;
      subAgent.canSeeMargin = !blocked.has('margin.view');
    }
  }

  function syncAllowance(id: string, allowance: Allowance) {
    const subAgent = find(id);

    if (subAgent) {
      subAgent.allowanceSpentMinor = allowance.spentMinor;
      subAgent.allowanceLimitMinor = allowance.limitMinor;
      subAgent.allowanceCurrency = allowance.currency;
    }
  }
}

/** The shared instance `main.tsx` uses in demo mode. */
export const mockSubAgentsApi = createMockSubAgentsApi();
