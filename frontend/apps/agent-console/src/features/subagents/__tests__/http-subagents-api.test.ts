import { createApiClient } from '@trips/api-client';
import { describe, expect, it } from 'vitest';
import { createHttpSubAgentsApi } from '../http-subagents-api';

function fakeServer(respond: (path: string, url: URL) => Response) {
  const requests: Array<{ method: string; path: string; body: unknown }> = [];

  const api = createApiClient({
    baseUrl: 'http://api.test',
    fetch: async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = input instanceof Request ? input : new Request(input, init);
      const text = await request.clone().text();
      const url = new URL(request.url);
      requests.push({
        method: request.method,
        path: url.pathname,
        body: text ? JSON.parse(text) : undefined,
      });
      return respond(url.pathname, url);
    },
  });

  return { api, requests };
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const member = {
  agencyId: '11111111-1111-4111-8111-111111111111',
  name: 'Ikeja Travel',
  status: 'Verified',
  isPrincipal: false,
  orders: 17,
  salesMinor: '5500000',
  allowanceSpentMinor: '640000',
  allowanceLimitMinor: '1000000',
};

describe('margin visibility comes from the response, never from a check here', () => {
  it('reads the margin when the API sent one', async () => {
    const { api } = fakeServer(() =>
      json({
        from: '2026-08-13T00:00:00Z',
        to: '2026-09-12T00:00:00Z',
        currency: 'NGN',
        orders: 17,
        salesMinor: '5500000',
        marginMinor: '440000',
        members: [{ ...member, marginMinor: '440000' }],
      }),
    );

    const performance = await createHttpSubAgentsApi({ api }).networkPerformance('a', 'b');

    expect(performance.marginMinor).toBe(440_000);
    expect(performance.members[0]?.marginMinor).toBe(440_000);
  });

  it('reports no margin when the key is absent, which is how the API says no', async () => {
    // The whole point of the two response shapes: a caller without `margin.view`
    // gets a body with no margin key in it at all, rather than one set to null.
    const { api } = fakeServer(() =>
      json({
        from: '2026-08-13T00:00:00Z',
        to: '2026-09-12T00:00:00Z',
        currency: 'NGN',
        orders: 17,
        salesMinor: '5500000',
        members: [member],
      }),
    );

    const performance = await createHttpSubAgentsApi({ api }).networkPerformance('a', 'b');

    expect(performance.marginMinor).toBeNull();
    expect(performance.members[0]?.marginMinor).toBeNull();

    // The rest of the figures still arrive — only the margin is missing.
    expect(performance.salesMinor).toBe(5_500_000);
    expect(performance.members[0]?.allowanceSpentMinor).toBe(640_000);
  });
});

describe('talking to the sub-agent endpoints', () => {
  it('sends an invitation as the API expects it', async () => {
    const { api, requests } = fakeServer(() =>
      json({
        id: '22222222-2222-4222-8222-222222222222',
        email: 'owner@ikeja.example.com',
        invitationToken: 'one-time-token',
        expiresAt: '2026-09-19T00:00:00Z',
      }),
    );

    const invited = await createHttpSubAgentsApi({ api }).invite({
      legalName: 'Ikeja Branch Limited',
      tradingName: null,
      email: 'owner@ikeja.example.com',
    });

    expect(invited.invitationToken).toBe('one-time-token');
    expect(requests[0]).toMatchObject({
      method: 'POST',
      path: '/api/v1/sub-agents',
      body: { legalName: 'Ikeja Branch Limited', tradingName: null },
    });
  });

  it('treats a 204 on the allowance as "they have none"', async () => {
    // Which is not the same as a limit of zero: one has never been set, and the
    // screen says so rather than showing a full bar.
    const { api } = fakeServer(() => new Response(null, { status: 204 }));

    const allowance = await createHttpSubAgentsApi({ api }).getAllowance('sub-1');

    expect(allowance).toBeNull();
  });

  it('sends a reason with a freeze, because the audit log needs one', async () => {
    const { api, requests } = fakeServer(() => new Response(null, { status: 204 }));

    await createHttpSubAgentsApi({ api }).freeze('sub-1', 'Unpaid balance.');

    expect(requests[0]).toMatchObject({
      method: 'POST',
      path: '/api/v1/sub-agents/sub-1/freeze',
      body: { reason: 'Unpaid balance.' },
    });
  });

  it('reads kobo amounts whether they arrive as numbers or as strings', async () => {
    const { api } = fakeServer(() =>
      json({
        subAgents: [
          {
            id: 'sub-1',
            legalName: 'Ikeja Branch Limited',
            tradingName: null,
            slug: 'ikeja-branch',
            status: 'Verified',
            statusReason: null,
            createdAt: '2026-07-02T09:00:00Z',
            hasOpenInvitation: false,
            scopeCount: 2,
            deniedPermissionCount: '1',
            canSeeMargin: false,
            allowanceSpentMinor: '640000',
            allowanceLimitMinor: 1_000_000,
            allowanceCurrency: 'NGN',
          },
        ],
        maxSubAgents: 5,
        canAddAnother: true,
        cannotAddReason: null,
      }),
    );

    const network = await createHttpSubAgentsApi({ api }).listSubAgents();

    expect(network.subAgents[0]?.allowanceSpentMinor).toBe(640_000);
    expect(network.subAgents[0]?.allowanceLimitMinor).toBe(1_000_000);
    expect(network.subAgents[0]?.deniedPermissionCount).toBe(1);
  });
});
