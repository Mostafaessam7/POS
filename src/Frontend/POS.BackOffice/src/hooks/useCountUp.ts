import { useEffect, useState } from "react";

const STEP_MS = 16;

/**
 * Animates from 0 to `target` over `durationMs` whenever `target` changes (and
 * isn't null). Driven by setInterval rather than requestAnimationFrame — rAF can
 * be suspended indefinitely by the browser for a backgrounded/non-composited tab,
 * which would leave the counter stuck at 0 until the tab regains focus; a timer
 * still fires (throttled, but not stopped) so the animation always completes.
 */
export function useCountUp(target: number | null, durationMs = 900): number {
  const [value, setValue] = useState(0);

  useEffect(() => {
    if (target === null) return;

    const start = Date.now();

    const interval = setInterval(() => {
      const elapsed = Date.now() - start;
      const progress = Math.min(1, elapsed / durationMs);
      // Ease-out cubic — fast start, gentle settle.
      const eased = 1 - Math.pow(1 - progress, 3);
      setValue(Math.round(target * eased));

      if (progress >= 1) {
        clearInterval(interval);
      }
    }, STEP_MS);

    return () => clearInterval(interval);
  }, [target, durationMs]);

  return value;
}
