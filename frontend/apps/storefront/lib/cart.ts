import { cookies } from 'next/headers';
import { commerce, type Cart, type StoreCall } from './api';

/**
 * The traveller's cart, and the cookie that finds it again.
 *
 * There are no traveller accounts (build plan decision 21), so a guest's cart is found by a session
 * token the API issues on their first add. That token is the whole of their identity here, which is
 * why it lives in an **http-only** cookie: a script on the page has no business reading it, and a
 * cart that outlives a tab is the least a shop can do.
 *
 * Every call goes through our own server rather than the browser, so the API is told the hostname
 * the traveller really arrived on — the only thing that decides whose shop they are in.
 */

/** The cookie the cart's session token lives in. */
export const CART_COOKIE = 'trips_cart';

/** How long the cookie lasts, in seconds. Comfortably longer than the cart it points at. */
const COOKIE_LIFETIME = 60 * 60 * 24 * 7;

/** The token in this request's cookie, if their browser has one. */
export async function cartToken(): Promise<string | undefined> {
  return (await cookies()).get(CART_COOKIE)?.value;
}

/**
 * Remembers the cart the API just handed back.
 *
 * Only callable from a server action or a route handler — Next refuses a cookie write during a page
 * render, which is right: rendering a page should not change anything.
 */
export async function rememberCart(token: string): Promise<void> {
  (await cookies()).set(CART_COOKIE, token, {
    httpOnly: true,
    sameSite: 'lax',
    secure: process.env.NODE_ENV === 'production',
    path: '/',
    maxAge: COOKIE_LIFETIME,
  });
}

/** Forgets the cart, once it has become an order. */
export async function forgetCart(): Promise<void> {
  (await cookies()).delete(CART_COOKIE);
}

/** What is in the traveller's cart, or null when they have not started one. */
export async function getCart(): Promise<Cart | null> {
  const token = await cartToken();

  if (!token) {
    return null;
  }

  const { data } = await commerce<Cart>('/cart', { method: 'GET', sessionToken: token });

  return data;
}

/** Puts something in the cart, starting one when there is none yet. */
export async function addToCart(
  item: { productId?: string; departureId?: string; offerId?: string },
  party: { adults: number; children: number; infants: number },
): Promise<StoreCall<Cart>> {
  const result = await commerce<Cart>('/cart/items', {
    method: 'POST',
    sessionToken: await cartToken(),
    body: {
      productId: item.productId ?? null,
      departureId: item.departureId ?? null,
      offerId: item.offerId ?? null,
      ...party,
    },
  });

  if (result.data?.sessionToken) {
    await rememberCart(result.data.sessionToken);
  }

  return result;
}

/** Takes a line out of the cart, giving back any seats it was holding. */
export async function removeFromCart(cartItemId: string): Promise<StoreCall<Cart>> {
  return commerce<Cart>(`/cart/items/${encodeURIComponent(cartItemId)}`, {
    method: 'DELETE',
    sessionToken: await cartToken(),
  });
}

/** How many things are in the cart, for the header. Zero when there is no cart. */
export async function cartCount(): Promise<number> {
  return (await getCart())?.items.length ?? 0;
}
