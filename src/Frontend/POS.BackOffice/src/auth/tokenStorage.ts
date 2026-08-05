// No token — access or refresh — is ever persisted here. The refresh token is an
// HttpOnly cookie the API sets (JS can never read it, closing the XSS-exfiltration
// gap a localStorage-held refresh token used to be); the access token lives only in
// memory (see accessToken.ts) and is re-minted via a silent refresh on page load.
//
// This cache exists purely so the UI (the top bar's "signed in as…") has something to
// show immediately on a hard reload, before that silent refresh resolves. None of these
// fields are secrets — losing them to XSS reveals only who the user is, not a
// credential that could act as them.
export interface StoredProfile {
  tenantId: string;
  userId: string;
  displayName: string;
  email: string;
  subdomain: string;
}

const STORAGE_KEY = "pos.backoffice.profile";

export function loadProfile(): StoredProfile | null {
  const raw = localStorage.getItem(STORAGE_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as StoredProfile;
  } catch {
    return null;
  }
}

export function saveProfile(profile: StoredProfile): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(profile));
}

export function clearProfile(): void {
  localStorage.removeItem(STORAGE_KEY);
}
