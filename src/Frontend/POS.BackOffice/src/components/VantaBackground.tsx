import { useEffect, useRef } from "react";
import * as THREE from "three";
import * as VantaNetModule from "vanta/dist/vanta.net.min";

// Vanta's UMD bundle sets `module.exports = { default: NET, __esModule: true }`.
// Vite's CJS interop doesn't always unwrap that automatically, so the real
// factory function can end up at either `.default` or `.default.default`
// depending on how the dep got pre-bundled — resolve whichever is callable.
type VantaNetFactory = (options: Record<string, unknown>) => { destroy: () => void };
const moduleExports = VantaNetModule as unknown as { default?: unknown };
const NET = (
  typeof moduleExports.default === "function"
    ? moduleExports.default
    : (moduleExports.default as { default?: unknown } | undefined)?.default
) as VantaNetFactory;

export function VantaBackground() {
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const effect = NET({
      el: containerRef.current,
      THREE,
      mouseControls: true,
      touchControls: true,
      gyroControls: false,
      minHeight: 200,
      minWidth: 200,
      scale: 1,
      scaleMobile: 1,
      color: 0x2453ff,
      backgroundColor: 0x070911,
      points: 11,
      maxDistance: 22,
      spacing: 18,
      showDots: true,
    });

    return () => effect.destroy();
  }, []);

  return <div ref={containerRef} className="vanta-background" aria-hidden="true" />;
}
