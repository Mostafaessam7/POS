import { getAccessToken, setAccessToken, type AccessToken } from "./accessToken";

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:9850";

export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  readonly fieldErrors?: Record<string, string[]>;

  constructor(status: number, code: string | undefined, message: string, fieldErrors?: Record<string, string[]>) {
    super(message);
    this.status = status;
    this.code = code;
    this.fieldErrors = fieldErrors;
  }
}

// Refresh-on-401 needs to happen exactly once even when several requests race into
// a 401 at the same moment (e.g. a page firing three concurrent GETs right as the
// 12-minute access token expires) — otherwise each one independently tries to
// rotate the SAME refresh token, and RefreshTokenService's reuse detection (by
// design) treats every rotation after the first as theft and revokes the whole
// family, logging the user out instead of quietly refreshing.
let refreshInFlight: Promise<AccessToken | null> | null = null;

/**
 * Mints a fresh access token from the HttpOnly refresh-token cookie. Used both for
 * the 401-triggered mid-session refresh below and for AuthContext's silent-refresh-
 * on-load bootstrap — the cookie is the same, credentialed request either way.
 */
export async function refreshSession(): Promise<AccessToken | null> {
  if (!refreshInFlight) {
    refreshInFlight = (async () => {
      try {
        let response: Response;
        try {
          response = await fetch(`${API_BASE_URL}/api/v1/auth/refresh`, {
            method: "POST",
            credentials: "include",
          });
        } catch {
          // The API being unreachable is not "not signed in" — treat it the same as
          // a rejected refresh (null) rather than letting the TypeError propagate.
          // Unwrapped, this throw used to escape AuthContext's bootstrap effect,
          // which never reaches its `setIsLoading(false)`, leaving the whole app
          // stuck rendering nothing (LoginPage and ProtectedRoute both bail out
          // while isLoading is true) with zero indication of why.
          setAccessToken(null);
          return null;
        }

        if (!response.ok) {
          setAccessToken(null);
          return null;
        }

        const body = (await response.json()) as { accessToken: string; expiresAt: string };
        const token: AccessToken = { accessToken: body.accessToken, expiresAt: body.expiresAt };
        setAccessToken(token);
        return token;
      } finally {
        refreshInFlight = null;
      }
    })();
  }

  return refreshInFlight;
}

interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  body?: unknown;
}

async function parseProblem(response: Response): Promise<ApiError> {
  let code: string | undefined;
  let message = `Request failed with status ${response.status}.`;
  let fieldErrors: Record<string, string[]> | undefined;

  try {
    const problem = await response.json();
    // FluentValidation's Results.ValidationProblem shape: { errors: { field: [msgs] } }
    if (problem.errors && typeof problem.errors === "object") {
      fieldErrors = problem.errors;
      message = problem.title ?? "One or more fields are invalid.";
    } else {
      // POS.Common.Errors.ErrorMapping's Results.Problem shape: type/title/detail,
      // with the domain Error.Code folded into "type" as a trailing segment.
      message = problem.detail ?? problem.title ?? message;
      code = typeof problem.type === "string" ? problem.type.split("/").pop() : undefined;
    }
  } catch {
    // Body wasn't JSON (e.g. a bare 401 from the auth middleware) — the generic
    // message above is all we can say.
  }

  return new ApiError(response.status, code, message, fieldErrors);
}

/**
 * Calls the API with the current access token attached, transparently refreshing
 * and retrying ONCE on a 401 (an expired access token, not a rejected credential —
 * a genuinely wrong or revoked credential also comes back as 401, and the retry
 * after a refresh failure is what turns that case into a clean sign-out instead of
 * a silent retry loop). `credentials: "include"` is required on every call — not
 * just the refresh — so the HttpOnly refresh-token cookie round-trips correctly.
 */
export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  // fetch() rejects (rather than resolving with a non-ok response) on a network-level
  // failure — the API unreachable, DNS/TLS broken, or the request blocked by CORS. Left
  // unwrapped, that TypeError isn't an ApiError, so every page's `err instanceof
  // ApiError ? err.message : "Failed to load …"` fallback collapses to the same generic
  // text regardless of cause, which is indistinguishable from a genuine 4xx/5xx in the
  // UI. Wrapping it here gives every caller a specific, actionable message for free.
  const doFetch = async (accessToken: string | undefined) => {
    try {
      return await fetch(`${API_BASE_URL}${path}`, {
        method: options.method ?? "GET",
        credentials: "include",
        headers: {
          "Content-Type": "application/json",
          ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
        },
        body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
      });
    } catch {
      throw new ApiError(
        0,
        "network.unreachable",
        `Couldn't reach the API at ${API_BASE_URL}. Check that the backend is running and reachable.`,
      );
    }
  };

  const existing = getAccessToken();
  let response = await doFetch(existing?.accessToken);

  if (response.status === 401) {
    const refreshed = await refreshSession();

    if (refreshed) {
      response = await doFetch(refreshed.accessToken);
    } else {
      // Refresh itself failed (expired/revoked/reused) — nothing left to try.
      // The caller (ProtectedRoute) is what actually redirects to /login; this
      // layer only reports the failure.
      throw new ApiError(401, "auth.session_expired", "Your session has expired. Please sign in again.");
    }
  }

  if (!response.ok) {
    throw await parseProblem(response);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export function apiGet<T>(path: string): Promise<T> {
  return apiFetch<T>(path);
}

export function apiPost<T>(path: string, body?: unknown): Promise<T> {
  return apiFetch<T>(path, { method: "POST", body });
}

export function apiPut<T>(path: string, body?: unknown): Promise<T> {
  return apiFetch<T>(path, { method: "PUT", body });
}

export function apiDelete<T>(path: string): Promise<T> {
  return apiFetch<T>(path, { method: "DELETE" });
}
