const STORAGE_KEY = "pos.terminalId";

/**
 * A stable per-browser terminal identity, generated once and reused.
 *
 * The backend doesn't validate TerminalId against a provisioned `Terminal` row (see
 * SalesEndpoints' remarks) — it's caller-supplied, the same trust level Shift and
 * Sale already give it in their own factories. A real deployment would provision
 * terminals explicitly and let the cashier pick one; this is the browser-as-till
 * simplification that goes with it.
 */
export function getTerminalId(): string {
  let id = localStorage.getItem(STORAGE_KEY);
  if (!id) {
    id = crypto.randomUUID();
    localStorage.setItem(STORAGE_KEY, id);
  }
  return id;
}
