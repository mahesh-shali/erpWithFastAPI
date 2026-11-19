/**
 * Full-window loading screen. Same markup and look as the static one in index.html (first frame) and the
 * update splash drawn while the installer runs, so launch → restore → last page reads as one screen.
 */
export function Splash({ message, detail }: { message: string; detail?: string }) {
  return (
    <div className="splash-screen" role="status" aria-live="polite">
      <div className="splash-inner">
        <svg className="splash-logo" viewBox="0 0 1024 1024" aria-hidden="true">
          <defs>
            <linearGradient id="splash-bg-r" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stopColor="#1e3a8a" /><stop offset="1" stopColor="#2f5bea" />
            </linearGradient>
          </defs>
          <rect x="32" y="32" width="960" height="960" rx="216" fill="url(#splash-bg-r)" />
          <g fill="#fff">
            <rect x="236" y="556" width="148" height="232" rx="36" fillOpacity=".72" />
            <rect x="438" y="420" width="148" height="368" rx="36" fillOpacity=".86" />
            <rect x="640" y="236" width="148" height="552" rx="36" />
          </g>
        </svg>
        <p className="splash-title">ERP Platform</p>
        <p className="splash-message">{message}</p>
        {detail && <p className="splash-detail">{detail}</p>}
        <div className="splash-bar" aria-hidden="true" />
      </div>
    </div>
  );
}
