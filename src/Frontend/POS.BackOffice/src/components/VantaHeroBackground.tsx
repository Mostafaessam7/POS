import { useEffect, useRef } from "react";
import * as THREE from "three";
import * as VantaHaloModule from "vanta/dist/vanta.halo.min";

// Same UMD-interop resolution as VantaBackground: the real factory function can
// land at `.default` or `.default.default` depending on how Vite pre-bundled it.
type VantaHaloFactory = (options: Record<string, unknown>) => { destroy: () => void };
const moduleExports = VantaHaloModule as unknown as { default?: unknown };
const HALO = (
  typeof moduleExports.default === "function"
    ? moduleExports.default
    : (moduleExports.default as { default?: unknown } | undefined)?.default
) as VantaHaloFactory;

export function VantaHeroBackground() {
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!containerRef.current) return;

    const effect = HALO({
      el: containerRef.current,
      THREE,
      mouseControls: true,
      touchControls: true,
      gyroControls: false,
      minHeight: 200,
      minWidth: 200,
      scale: 1,
      scaleMobile: 1,
      backgroundColor: 0x0b0e18,
      baseColor: 0x3358ff,
      size: 1.4,
      amplitudeFactor: 1.8,
    });

    return () => effect.destroy();
  }, []);

  return <div ref={containerRef} className="dashboard-hero__vanta" aria-hidden="true" />;
}
