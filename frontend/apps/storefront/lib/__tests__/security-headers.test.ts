import { describe, expect, it } from 'vitest';
import { NextRequest } from 'next/server';
import config from '../../next.config';
import { middleware } from '../../middleware';
import { contentSecurityPolicy, fixedSecurityHeaders } from '../security-headers';

/**
 * The headers on a traveller's page (issue 107), so a later change to the middleware — the thing
 * that silently drops them — fails here rather than in production.
 */

const request = (url = 'https://lagos-travel.example/') => new NextRequest(new Request(url));

describe('the fixed headers', () => {
  it('are configured for every path, and name nothing of ours', async () => {
    const headers = await config.headers?.();

    expect(headers?.[0]?.source).toBe('/:path*');
    expect(headers?.[0]?.headers).toEqual(fixedSecurityHeaders);
    expect(config.poweredByHeader).toBe(false);
  });

  it('cover sniffing, framing, referrers, features and transport', () => {
    const byKey = Object.fromEntries(fixedSecurityHeaders.map((h) => [h.key, h.value]));

    expect(byKey['X-Content-Type-Options']).toBe('nosniff');
    expect(byKey['X-Frame-Options']).toBe('DENY');
    expect(byKey['Referrer-Policy']).toBe('strict-origin-when-cross-origin');
    expect(byKey['Permissions-Policy']).toContain('camera=()');

    // A month, and never preloaded: preload is close to irreversible on an agency's own domain.
    expect(byKey['Strict-Transport-Security']).toBe('max-age=2592000');
    expect(byKey['Strict-Transport-Security']).not.toContain('preload');
  });
});

describe('the content security policy', () => {
  it('is on the response, and on the request so Next can read the nonce back', () => {
    const response = middleware(request());

    const policy = response.headers.get('Content-Security-Policy');
    expect(policy).toContain("frame-ancestors 'none'");
    expect(policy).toMatch(/script-src 'self' 'nonce-[^']+' 'strict-dynamic'/);
    expect(response.headers.get('x-middleware-override-headers')).toContain(
      'content-security-policy',
    );
  });

  it('gives every response a nonce of its own', () => {
    const first = middleware(request()).headers.get('Content-Security-Policy');
    const second = middleware(request()).headers.get('Content-Security-Policy');

    expect(first).not.toBe(second);
  });

  it('allows what the site as built actually needs', () => {
    const policy = contentSecurityPolicy('n0nce', false);

    // The agency's brand colour is a style attribute on <body>; styles run no code.
    expect(policy).toContain("style-src 'self' 'unsafe-inline'");
    // Logos and product photos are signed URLs on whatever host the assets live on. Naming that
    // host in the header would print it on the agency's own domain — CLAUDE.md rule 4.
    expect(policy).toContain('img-src');
    expect(policy).toContain('https:');
    // Checkout without JavaScript posts the form and is redirected to the gateway's hosted page.
    expect(policy).toContain("form-action 'self' https://checkout.paystack.com");
    expect(policy).not.toContain("script-src 'self' 'unsafe-inline'");
  });

  it('says nothing about who built the site', () => {
    const policy = contentSecurityPolicy('n0nce', false);

    expect(policy.toLowerCase()).not.toContain('trips');
  });

  it('keeps carrying a preview token', () => {
    const response = middleware(request('https://lagos-travel.example/?preview=abc123'));

    expect(response.cookies.get('storefront-preview')?.value).toBe('abc123');
  });
});
