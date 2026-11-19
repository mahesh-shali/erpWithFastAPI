import { useEffect, useRef, useState } from 'react';
import type { UpdateStatus } from '../../electron/shared/contract';
import { Icon } from './Icon';

/**
 * Top-bar update symbol. The main process checks GitHub Releases in the background; a dot appears when a
 * newer version is out. The panel lets the user check, download (with progress) and restart into it.
 */
export function UpdateButton() {
  const [status, setStatus] = useState<UpdateStatus>({ state: 'idle' });
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const current = window.erp.app.versions.app;

  useEffect(() => {
    // A pushed status is newer than the initial fetch, so once one arrives the fetch result is ignored.
    let live = true;
    let pushed = false;
    const off = window.erp.updates.onStatus((s) => { pushed = true; setStatus(s); });
    window.erp.updates.getStatus().then((s) => live && !pushed && setStatus(s)).catch(() => undefined);
    return () => { live = false; off(); };
  }, []);

  // Close on Escape or a click outside the panel.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    const onClick = (e: MouseEvent) => { if (!wrap.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onClick);
    return () => { document.removeEventListener('keydown', onKey); document.removeEventListener('mousedown', onClick); };
  }, [open]);

  const ready = status.state === 'available' || status.state === 'downloaded';
  const label = status.state === 'available' ? `Update available: version ${status.version}`
    : status.state === 'downloaded' ? `Update ready: version ${status.version}`
    : status.state === 'downloading' ? `Downloading update: ${status.percent}%`
    : 'Updates';

  const run = (fn: () => Promise<UpdateStatus>) => void fn().then(setStatus).catch(() => undefined);

  return (
    <div className="update-wrap" ref={wrap}>
      <button className="icon-btn update-btn" aria-label={label} title={label} aria-expanded={open}
              aria-haspopup="dialog" onClick={() => setOpen((o) => !o)}>
        <Icon name="download" size={20} />
        {ready && <span className="update-dot" aria-hidden="true" />}
        {status.state === 'downloading' && <span className="update-pct" aria-hidden="true">{status.percent}%</span>}
      </button>

      {open && (
        <div className="update-panel" role="dialog" aria-label="App updates">
          <p className="update-current">Installed version <strong>{current || '—'}</strong></p>
          <Body status={status}
                onCheck={() => run(() => window.erp.updates.check())}
                onDownload={() => run(() => window.erp.updates.download())}
                onInstall={() => void window.erp.updates.install()}
                onLater={() => setOpen(false)} />
        </div>
      )}
    </div>
  );
}

function Body({ status, onCheck, onDownload, onInstall, onLater }: {
  status: UpdateStatus; onCheck: () => void; onDownload: () => void; onInstall: () => void; onLater: () => void;
}) {
  switch (status.state) {
    case 'unsupported':
      return <p className="hint">{status.reason}</p>;
    case 'checking':
      return <p className="hint">Checking for updates…</p>;
    case 'available':
      return (
        <>
          <p><strong>Version {status.version}</strong> is available.</p>
          {status.releaseNotes && <pre className="update-notes">{status.releaseNotes}</pre>}
          <button onClick={onDownload}>Download update</button>
        </>
      );
    case 'downloading':
      return (
        <>
          <p>Downloading version {status.version}…</p>
          <progress className="update-progress" max={100} value={status.percent} aria-label="Download progress" />
          <p className="hint">{status.percent}%</p>
        </>
      );
    case 'downloaded':
      return (
        <>
          <p><strong>Version {status.version}</strong> is ready to install.</p>
          <div className="row">
            <button onClick={onInstall}>Restart now</button>
            <button className="link" onClick={onLater}>Later</button>
          </div>
          <p className="hint">If you choose Later, it installs when you close the app.</p>
        </>
      );
    case 'error':
      return (
        <>
          <p role="alert" className="error">{status.message}</p>
          <button onClick={onCheck}>Try again</button>
        </>
      );
    case 'up-to-date':
      return (
        <>
          <p>You're on the latest version.</p>
          <p className="hint">Checked at {new Date(status.checkedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</p>
          <button className="link" onClick={onCheck}>Check again</button>
        </>
      );
    default:
      return <button onClick={onCheck}>Check for updates</button>;
  }
}
