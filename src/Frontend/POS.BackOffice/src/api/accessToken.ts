// The access token lives ONLY in memory — never localStorage, never sessionStorage.
// The refresh token that can mint a new one is an HttpOnly cookie the API sets and
// this code can never read; losing the in-memory access token on a hard refresh is
// the accepted cost of that, recovered via a silent POST /auth/refresh on load (see
// AuthContext's bootstrap effect).
export interface AccessToken {
  accessToken: string;
  expiresAt: string;
}

let current: AccessToken | null = null;

export function getAccessToken(): AccessToken | null {
  return current;
}

export function setAccessToken(token: AccessToken | null): void {
  current = token;
}
