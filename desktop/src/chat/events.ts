import type { ChatEvent } from './api';

/**
 * Live chat notices for the DESKTOP app. The renderer has no network access (CSP connect-src 'none'), so the
 * event stream runs in the Electron main process (electron/main/chat-stream.ts), which forwards validated
 * notices over IPC. Same signature as the web app's events.ts.
 */
export function subscribeChatEvents(listener: (e: ChatEvent) => void): () => void {
  const off = window.erp.chat.onEvent(listener);
  void window.erp.chat.start();
  return () => {
    off();
    void window.erp.chat.stop();
  };
}
