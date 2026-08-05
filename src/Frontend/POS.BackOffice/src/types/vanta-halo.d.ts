declare module "vanta/dist/vanta.halo.min" {
  interface VantaEffect {
    destroy: () => void;
  }

  interface VantaHaloOptions {
    el: HTMLElement;
    THREE?: unknown;
    mouseControls?: boolean;
    touchControls?: boolean;
    gyroControls?: boolean;
    minHeight?: number;
    minWidth?: number;
    scale?: number;
    scaleMobile?: number;
    backgroundColor?: number;
    baseColor?: number;
    size?: number;
    amplitudeFactor?: number;
    xOffset?: number;
    yOffset?: number;
  }

  const HALO: (options: VantaHaloOptions) => VantaEffect;
  export default HALO;
}
