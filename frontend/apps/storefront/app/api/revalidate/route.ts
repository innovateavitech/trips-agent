import { revalidateTag } from 'next/cache';
import { NextResponse } from 'next/server';

/**
 * "This site has been published — drop what you are holding for it."
 *
 * Called by the API after a publish or a change of hostname (`IStorefrontRevalidator`). Without it a
 * traveller would keep seeing last week's prices until a timer ran out; with it, publishing is
 * visible on the next page load.
 *
 * Authenticated by a shared secret in a header, because this endpoint is the one way to make the
 * storefront do work on demand and it is reachable from the open internet. There is nothing to leak
 * here — the worst an attacker can do is make us re-fetch pages — but making that free would be a
 * way to load the API up, so the secret is checked in constant time and a bad one gets 401 with
 * nothing else said.
 */

const TOKEN_HEADER = 'x-revalidate-token';

interface RevalidateRequest {
  siteId?: string;
  hostnames?: string[];
}

export async function POST(request: Request): Promise<NextResponse> {
  const expected = process.env.REVALIDATE_TOKEN;

  if (!expected) {
    // Not configured is not "allow everything": it is refused, loudly, in the log.
    console.error('REVALIDATE_TOKEN is not set, so rebuild requests cannot be authenticated.');
    return NextResponse.json({ error: 'Not configured.' }, { status: 503 });
  }

  if (!matches(request.headers.get(TOKEN_HEADER), expected)) {
    return NextResponse.json({ error: 'Unauthorised.' }, { status: 401 });
  }

  let body: RevalidateRequest;

  try {
    body = (await request.json()) as RevalidateRequest;
  } catch {
    return NextResponse.json({ error: 'Expected a JSON body.' }, { status: 400 });
  }

  const tags: string[] = [];

  if (body.siteId) {
    tags.push(`site:${body.siteId}`);
  }

  for (const hostname of body.hostnames ?? []) {
    // Tagged the same way lib/api tags what it fetched: lower case, no port.
    tags.push(`host:${hostname.trim().toLowerCase()}`);
  }

  if (tags.length === 0) {
    return NextResponse.json({ error: 'Name a site or a hostname to rebuild.' }, { status: 400 });
  }

  for (const tag of tags) {
    revalidateTag(tag);
  }

  return NextResponse.json({ revalidated: tags });
}

/**
 * Compares the two secrets without giving away how much of a guess was right.
 *
 * A `===` on strings stops at the first different character, and how long it took says how many
 * characters matched — enough, over many tries, to work the secret out one character at a time.
 */
function matches(given: string | null, expected: string): boolean {
  if (!given || given.length !== expected.length) {
    return false;
  }

  let difference = 0;

  for (let index = 0; index < expected.length; index += 1) {
    difference |= given.charCodeAt(index) ^ expected.charCodeAt(index);
  }

  return difference === 0;
}
