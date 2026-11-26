import { useLayoutEffect, useRef } from 'react';

/**
 * Shows an organization's rendered print template, isolated in a shadow root so its CSS and the app's never mix.
 * The API has already sanitized the HTML (no scripts, handlers or external URLs) and turned inline styles into
 * classes. The CSS goes into a constructed stylesheet: the web CSP (style-src 'self') blocks <style> elements and
 * style attributes, but not stylesheets built through the CSSOM. Assigning innerHTML never runs scripts.
 */
export function TemplateView({ html, css, label }: { html: string; css: string; label: string }) {
  const host = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const el = host.current;
    if (!el) return;
    const root = el.shadowRoot ?? el.attachShadow({ mode: 'open' });
    root.innerHTML = html;
    if (typeof CSSStyleSheet !== 'undefined' && 'replaceSync' in CSSStyleSheet.prototype && 'adoptedStyleSheets' in root) {
      const sheet = new CSSStyleSheet();
      sheet.replaceSync(css);
      root.adoptedStyleSheets = [sheet];
    }
  }, [html, css]);
  return <div ref={host} className="template-view" role="document" aria-label={label} />;
}
