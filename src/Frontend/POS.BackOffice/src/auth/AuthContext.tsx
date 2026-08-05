import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { setAccessToken } from "../api/accessToken";
import { ApiError, apiPost, refreshSession } from "../api/client";
import { clearProfile, loadProfile, saveProfile, type StoredProfile } from "./tokenStorage";

interface LoginResponse {
  accessToken: string;
  expiresAt: string;
  tenantId: string;
  userId: string;
  displayName: string;
  email: string;
}

interface AuthContextValue {
  session: StoredProfile | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (subdomain: string, email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<StoredProfile | null>(null);

  // Separate from `session`: the cached profile is shown optimistically on load so
  // the top bar doesn't flash empty, but whether the user is ACTUALLY authenticated
  // depends on whether the silent refresh below succeeds, not on a profile existing
  // in localStorage — those can disagree (a cleared cache with a still-valid cookie,
  // or a stale cache with an expired one), and this must never trust the cache alone.
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [isLoading, setIsLoading] = useState(true);

  // The access token lives only in memory, so a hard reload always starts with
  // none — the only way to know whether the user is still signed in is to ask the
  // API, via the HttpOnly refresh-token cookie it can see but this code cannot.
  useEffect(() => {
    let cancelled = false;

    (async () => {
      const cachedProfile = loadProfile();
      if (cachedProfile) setSession(cachedProfile);

      const refreshed = await refreshSession();

      if (cancelled) return;

      if (refreshed) {
        setIsAuthenticated(true);
      } else {
        clearProfile();
        setSession(null);
        setIsAuthenticated(false);
      }

      setIsLoading(false);
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      session,
      isAuthenticated,
      isLoading,
      async login(subdomain: string, email: string, password: string) {
        const response = await apiPost<LoginResponse>("/api/v1/auth/login", {
          subdomain,
          email,
          password,
        });

        setAccessToken({ accessToken: response.accessToken, expiresAt: response.expiresAt });

        const profile: StoredProfile = {
          tenantId: response.tenantId,
          userId: response.userId,
          displayName: response.displayName,
          email: response.email,
          subdomain,
        };

        saveProfile(profile);
        setSession(profile);
        setIsAuthenticated(true);
      },
      async logout() {
        try {
          await apiPost("/api/v1/auth/logout");
        } catch {
          // Best-effort: the user's intent to leave must succeed locally even if
          // the network call fails — there is nothing else this layer can do about
          // a revoke that never reached the server.
        } finally {
          setAccessToken(null);
          clearProfile();
          setSession(null);
          setIsAuthenticated(false);
        }
      },
    }),
    [session, isAuthenticated, isLoading],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth must be used within an AuthProvider");
  return context;
}

export { ApiError };
