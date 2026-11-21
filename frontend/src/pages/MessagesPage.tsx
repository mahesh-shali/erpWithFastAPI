import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { ApiError, type UserDto } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { chatApi, type ChatContact, type ChatConversation, type ChatKeyBundle, type ChatMessageDto } from '../chat/api';
import {
  changePassphrase, createIdentity, decryptMessage, encryptMessage, fingerprint, unlockIdentity,
  WrongPassphraseError, type UnlockedKey,
} from '../chat/crypto';
import { forgetDeviceKeys, loadDeviceKey, saveDeviceKey } from '../chat/deviceKeys';
import { subscribeChatEvents } from '../chat/events';

/**
 * Messages: end-to-end encrypted chat. Identical in the web and desktop apps (only chat/events.ts differs).
 *
 * Who can talk to whom is decided by the server (see ChatService): everyone in the organization chats in
 * direct and group conversations, but starting one with a more senior role needs the "Message Senior Roles"
 * right. General is an org-wide channel for everyone; only the SuperAdmin posts to Announcements.
 * Encryption and decryption happen only here, in the client (chat/crypto.ts).
 */

const MIN_PASSPHRASE = 10;
const MAX_TEXT = 4000;
const POLL_MS = 45_000; // safety net if the live stream is blocked by a proxy

const errorText = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong');

export function MessagesPage() {
  const { user } = useAuth();
  const [bundle, setBundle] = useState<ChatKeyBundle | null | undefined>(undefined); // undefined = loading
  const [key, setKey] = useState<UnlockedKey | null>(null);
  const [resetting, setResetting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!user) return;
    setError(null);
    try {
      const b = (await chatApi.myKey()) ?? null;
      const stored = b ? await loadDeviceKey(user.id) : null;
      if (b && stored?.keyId === b.keyId && stored.publicKey === b.publicKey)
        setKey({ keyId: stored.keyId, publicKey: stored.publicKey, privateKey: stored.privateKey });
      else if (stored) await forgetDeviceKeys(user.id); // the key was reset on another device
      setBundle(b);
    } catch (e) {
      setError(errorText(e));
    }
  }, [user]);

  useEffect(() => { void load(); }, [load]);

  const unlocked = useCallback(async (b: ChatKeyBundle, privateKey: CryptoKey) => {
    await saveDeviceKey({ userId: user!.id, keyId: b.keyId, publicKey: b.publicKey, privateKey });
    setResetting(false);
    setBundle(b);
    setKey({ keyId: b.keyId, publicKey: b.publicKey, privateKey });
  }, [user]);

  const lock = useCallback(async () => {
    await forgetDeviceKeys(user!.id);
    setKey(null);
  }, [user]);

  let body;
  if (error) body = <p className="error card">{error}</p>;
  else if (bundle === undefined || !user) body = <p className="hint">Loading…</p>;
  else if (key && bundle) body = <ChatWorkspace user={user} myKey={key} bundle={bundle} onBundle={setBundle} onLock={lock} />;
  else if (bundle === null) body = <KeySetup mode="setup" onDone={unlocked} onConflict={load} />;
  else if (resetting) body = <KeySetup mode="reset" onDone={unlocked} onCancel={() => setResetting(false)} />;
  else body = <KeyUnlock bundle={bundle} onDone={unlocked} onForgot={() => setResetting(true)} />;

  return (
    <>
      <div className="page-head">
        <h1>Messages</h1>
        <span className="e2e-badge" title="Messages are encrypted on your device and can only be read by the people in the conversation.">
          End-to-end encrypted
        </span>
      </div>
      {body}
    </>
  );
}

// ---------------- key setup / unlock ----------------

function KeySetup({ mode, onDone, onConflict, onCancel }: {
  mode: 'setup' | 'reset';
  onDone: (b: ChatKeyBundle, k: CryptoKey) => Promise<void>;
  onConflict?: () => void;
  onCancel?: () => void;
}) {
  const [pass, setPass] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (pass.length < MIN_PASSPHRASE) return setError(`Use at least ${MIN_PASSPHRASE} characters.`);
    if (pass !== confirm) return setError('The passphrases do not match.');
    setBusy(true);
    setError(null);
    try {
      const { backup, privateKey } = await createIdentity(pass);
      const b = mode === 'setup' ? await chatApi.registerKey(backup) : await chatApi.resetKey(backup);
      await onDone(b, privateKey);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && onConflict) onConflict();
      else setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card key-form" onSubmit={submit}>
      <h2>{mode === 'setup' ? 'Set up secure messaging' : 'Reset your secure messaging key'}</h2>
      {mode === 'setup' ? (
        <p className="hint">
          Choose a passphrase. It protects your private key, which is stored only in encrypted form, so you can
          read your messages on the website and in the desktop app. Nobody can recover it for you, not even an administrator.
        </p>
      ) : (
        <p className="error">
          A new key can't read messages sent to your old key. Your earlier messages will show as unreadable. Do this
          only if you have forgotten your passphrase.
        </p>
      )}
      <label>New passphrase
        <input type="password" autoComplete="new-password" value={pass} onChange={(e) => setPass(e.target.value)} required />
      </label>
      <label>Repeat passphrase
        <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
      </label>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions">
        <button type="submit" disabled={busy}>{busy ? 'Creating keys…' : mode === 'setup' ? 'Set up' : 'Reset key'}</button>
        {onCancel && <button type="button" className="link" onClick={onCancel}>Cancel</button>}
      </div>
    </form>
  );
}

function KeyUnlock({ bundle, onDone, onForgot }: {
  bundle: ChatKeyBundle; onDone: (b: ChatKeyBundle, k: CryptoKey) => Promise<void>; onForgot: () => void;
}) {
  const [pass, setPass] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await onDone(bundle, await unlockIdentity(bundle, pass));
    } catch (err) {
      setError(err instanceof WrongPassphraseError ? err.message : errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card key-form" onSubmit={submit}>
      <h2>Unlock your messages</h2>
      <p className="hint">Enter your secure messaging passphrase. It stays on this device until you sign out.</p>
      <label>Passphrase
        <input type="password" autoComplete="current-password" value={pass} onChange={(e) => setPass(e.target.value)} required autoFocus />
      </label>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions">
        <button type="submit" disabled={busy || !pass}>{busy ? 'Unlocking…' : 'Unlock'}</button>
        <button type="button" className="link" onClick={onForgot}>Forgot your passphrase?</button>
      </div>
    </form>
  );
}

// ---------------- workspace ----------------

interface Shown {
  id: number; senderId: string | null; senderName: string; senderPublicKey: string;
  text: string | null; error: string | null; createdAt: string;
}
interface Thread { messages: Shown[]; hasOlder: boolean }

export function conversationTitle(c: ChatConversation, meId: string) {
  if (c.kind === 'announcement') return 'Announcements';
  if (c.kind === 'general') return 'General';
  if (c.kind === 'group') return c.title ?? 'Group';
  return c.members.find((m) => m.userId !== meId)?.fullName ?? 'Direct message';
}

function conversationSubtitle(c: ChatConversation, meId: string) {
  if (c.kind === 'announcement') return 'From the organization owner to everyone';
  if (c.kind === 'general') return 'Everyone in your organization';
  if (c.kind === 'group') return `${c.members.length} members`;
  return c.members.find((m) => m.userId !== meId)?.role ?? '';
}

/** Sort rank: Announcements, then General, above everything else. */
const pinRank = (c: ChatConversation) => (c.kind === 'announcement' ? 2 : c.kind === 'general' ? 1 : 0);

const timeOf = (iso: string) => {
  const d = new Date(iso);
  return d.toDateString() === new Date().toDateString()
    ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleString([], { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
};

function ChatWorkspace({ user, myKey, bundle, onBundle, onLock }: {
  user: UserDto; myKey: UnlockedKey; bundle: ChatKeyBundle;
  onBundle: (b: ChatKeyBundle) => void; onLock: () => void;
}) {
  const isOwner = user.role === 'SuperAdmin';
  const [conversations, setConversations] = useState<ChatConversation[] | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [threads, setThreads] = useState<Record<string, Thread>>({});
  const [composing, setComposing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const selectedRef = useRef<string | null>(null);
  const threadsRef = useRef(threads);
  selectedRef.current = selectedId;
  threadsRef.current = threads;

  const selected = conversations?.find((c) => c.id === selectedId) ?? null;

  const loadConversations = useCallback(async () => {
    try {
      const list = await chatApi.conversations();
      // Org-wide channels first (Announcements, then General), then most recent activity
      list.sort((a, b) => pinRank(b) - pinRank(a) || b.lastMessageAt.localeCompare(a.lastMessageAt));
      setConversations(list);
      setError(null);
    } catch (e) {
      setError(errorText(e));
    }
  }, []);

  const decrypt = useCallback(async (m: ChatMessageDto): Promise<Shown> => {
    const base = { id: m.id, senderId: m.senderId, senderName: m.senderName, senderPublicKey: m.senderPublicKey, createdAt: m.createdAt };
    try {
      return { ...base, text: (await decryptMessage(myKey, m)).text, error: null };
    } catch (e) {
      return { ...base, text: null, error: errorText(e) };
    }
  }, [myKey]);

  const loadThread = useCallback(async (id: string, mode: 'initial' | 'older' | 'newer') => {
    const current = threadsRef.current[id];
    if (mode !== 'initial' && !current) mode = 'initial';
    const page = mode === 'initial' ? await chatApi.messages(id)
      : mode === 'older' ? await chatApi.messages(id, { before: current!.messages[0]?.id })
      : await chatApi.messages(id, { after: current!.messages.at(-1)?.id ?? 0 });
    const shown = await Promise.all(page.map(decrypt));
    setThreads((t) => {
      const prev = t[id]?.messages ?? [];
      const merged = mode === 'initial' ? shown : mode === 'older' ? [...shown, ...prev] : [...prev, ...shown];
      const unique = [...new Map(merged.map((m) => [m.id, m])).values()].sort((a, b) => a.id - b.id);
      return {
        ...t,
        [id]: { messages: unique, hasOlder: mode === 'newer' ? (t[id]?.hasOlder ?? false) : page.length >= 50 },
      };
    });
  }, [decrypt]);

  useEffect(() => { void loadConversations(); }, [loadConversations]);

  useEffect(() => {
    if (selectedId) void loadThread(selectedId, threadsRef.current[selectedId] ? 'newer' : 'initial').catch((e) => setError(errorText(e)));
  }, [selectedId, loadThread]);

  // Live updates (notices only; content is fetched and decrypted here) + a slow poll as a safety net.
  useEffect(() => {
    const refresh = (conversationId?: string | null) => {
      void loadConversations();
      const open = selectedRef.current;
      if (open && (!conversationId || conversationId === open)) void loadThread(open, 'newer').catch(() => undefined);
    };
    const off = subscribeChatEvents((e) => {
      if (e.type === 'message') refresh(e.conversationId);
      else if (e.type === 'conversation' || e.type === 'reconnected') refresh();
    });
    const timer = setInterval(() => refresh(), POLL_MS);
    const onVisible = () => document.visibilityState === 'visible' && refresh();
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      off();
      clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [loadConversations, loadThread]);

  // Mark the open conversation as read up to its newest message.
  const lastId = selectedId ? threads[selectedId]?.messages.at(-1)?.id : undefined;
  useEffect(() => {
    if (!selected || lastId === undefined || lastId <= selected.lastReadMessageId) return;
    const id = selected.id;
    setConversations((list) => list?.map((c) => (c.id === id ? { ...c, unread: 0, lastReadMessageId: lastId } : c)) ?? list);
    void chatApi.markRead(id, lastId).catch(() => undefined);
  }, [selected, lastId]);

  async function started(c: ChatConversation) {
    setComposing(false);
    await loadConversations();
    setSelectedId(c.id);
  }

  const appendSent = useCallback((m: ChatMessageDto, text: string) => {
    setThreads((t) => {
      const prev = t[m.conversationId] ?? { messages: [], hasOlder: false };
      if (prev.messages.some((x) => x.id === m.id)) return t;
      const mine: Shown = {
        id: m.id, senderId: m.senderId, senderName: m.senderName, senderPublicKey: m.senderPublicKey,
        text, error: null, createdAt: m.createdAt,
      };
      return { ...t, [m.conversationId]: { ...prev, messages: [...prev.messages, mine] } };
    });
    void loadConversations();
  }, [loadConversations]);

  return (
    <div className={`messenger card${selected || composing ? ' has-thread' : ''}`}>
      <aside className="conv-list" aria-label="Conversations">
        <div className="conv-list-head">
          <strong>Conversations</strong>
          <button type="button" onClick={() => { setComposing(true); setSelectedId(null); }}>New</button>
        </div>
        {error && <p className="error" role="alert">{error}</p>}
        {!conversations && !error && <p className="hint">Loading…</p>}
        <ul>
          {conversations?.map((c) => (
            <li key={c.id}>
              <button type="button" className={`conv-item${c.id === selectedId ? ' active' : ''}`}
                      onClick={() => { setComposing(false); setSelectedId(c.id); }}>
                <span className="conv-name">{conversationTitle(c, user.id)}</span>
                <span className="conv-sub">{conversationSubtitle(c, user.id)}</span>
                {c.unread > 0 && <span className="unread" aria-label={`${c.unread} unread`}>{c.unread}</span>}
              </button>
            </li>
          ))}
        </ul>
        <SecurityPanel myKey={myKey} bundle={bundle} onBundle={onBundle} onLock={onLock} />
      </aside>

      <section className="thread" aria-label="Conversation">
        {composing ? (
          <NewConversation isOwner={isOwner} onStarted={started} onCancel={() => setComposing(false)} />
        ) : selected ? (
          <ThreadView
            key={selected.id} conversation={selected} me={user} myKey={myKey}
            thread={threads[selected.id]} onOlder={() => loadThread(selected.id, 'older')}
            onSent={appendSent} onBack={() => setSelectedId(null)}
          />
        ) : (
          <p className="hint thread-empty">Choose a conversation or start a new one.</p>
        )}
      </section>
    </div>
  );
}

function ThreadView({ conversation: c, me, myKey, thread, onOlder, onSent, onBack }: {
  conversation: ChatConversation; me: UserDto; myKey: UnlockedKey; thread: Thread | undefined;
  onOlder: () => Promise<void>; onSent: (m: ChatMessageDto, text: string) => void; onBack: () => void;
}) {
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [verify, setVerify] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  const count = thread?.messages.length ?? 0;

  useEffect(() => { endRef.current?.scrollIntoView?.({ block: 'end' }); }, [count]);

  async function send() {
    const body = text.trim();
    if (!body || busy) return;
    setBusy(true);
    setError(null);
    try {
      for (let attempt = 0; ; attempt++) {
        // Encrypt for exactly the members' current keys; the server rejects anything else with 409.
        const { recipients, withoutKey } = await chatApi.recipients(c.id);
        const sealed = await encryptMessage(myKey, c.id, { text: body },
          recipients.map((r) => ({ keyId: r.keyId!, publicKey: r.publicKey! })));
        try {
          onSent(await chatApi.send(c.id, myKey.keyId, sealed), body);
          setText('');
          setNotice(withoutKey.length
            ? `${withoutKey.join(', ')} ${withoutKey.length === 1 ? 'has' : 'have'} not set up secure messaging yet and can't read this message.`
            : null);
          return;
        } catch (e) {
          if (!(e instanceof ApiError && e.status === 409 && attempt === 0)) throw e; // keys changed meanwhile: retry once
        }
      }
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void send();
    }
  }

  return (
    <>
      <header className="thread-head">
        <button type="button" className="link back" onClick={onBack}>‹ Back</button>
        <div>
          <h2>{conversationTitle(c, me.id)}</h2>
          <p className="hint">
            {c.kind === 'announcement' || c.kind === 'general' ? 'Everyone in your organization receives these messages.'
              : c.members.map((m) => (m.userId === me.id ? 'You' : m.fullName)).join(', ')}
          </p>
        </div>
        <button type="button" className="link" onClick={() => setVerify((v) => !v)} aria-expanded={verify}>Verify keys</button>
      </header>

      {verify && <VerifyKeys conversation={c} myKey={myKey} messages={thread?.messages ?? []} />}

      <div className="thread-messages" role="log" aria-live="polite">
        {thread?.hasOlder && (
          <button type="button" className="link older" onClick={() => void onOlder().catch((e) => setError(errorText(e)))}>
            Load earlier messages
          </button>
        )}
        {!thread && <p className="hint">Loading…</p>}
        {thread && count === 0 && <p className="hint">No messages yet.</p>}
        {thread?.messages.map((m) => {
          const mine = m.senderId === me.id;
          return (
            <div key={m.id} className={`bubble${mine ? ' mine' : ''}${m.error ? ' locked' : ''}`} data-testid="chat-message">
              {!mine && <span className="who">{m.senderName}</span>}
              <p>{m.text ?? m.error}</p>
              <time dateTime={m.createdAt}>{timeOf(m.createdAt)}</time>
            </div>
          );
        })}
        <div ref={endRef} />
      </div>

      {notice && <p className="hint notice">{notice}</p>}
      {error && <p className="error" role="alert">{error}</p>}
      {c.canPost ? (
        <form className="composer" onSubmit={(e) => { e.preventDefault(); void send(); }}>
          <textarea
            aria-label="Message" placeholder={c.kind === 'announcement' ? 'Write an announcement…' : 'Write a message…'}
            value={text} maxLength={MAX_TEXT} rows={2} onChange={(e) => setText(e.target.value)} onKeyDown={onKeyDown}
          />
          <button type="submit" disabled={busy || !text.trim()}>{busy ? 'Sending…' : 'Send'}</button>
        </form>
      ) : (
        <p className="hint read-only">Only the organization owner can post here.</p>
      )}
    </>
  );
}

/** Fingerprints computed here from the exact public keys used for encryption/decryption. */
function VerifyKeys({ conversation: c, myKey, messages }: { conversation: ChatConversation; myKey: UnlockedKey; messages: Shown[] }) {
  const [rows, setRows] = useState<{ name: string; fp: string }[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const senders = useMemo(
    () => [...new Map(messages.map((m) => [m.senderPublicKey, m.senderName])).entries()],
    [messages]);

  useEffect(() => {
    let live = true;
    (async () => {
      const people: [string, string][] = c.canPost
        ? (await chatApi.recipients(c.id)).recipients.filter((r) => r.keyId !== myKey.keyId).map((r) => [r.publicKey!, r.fullName])
        : senders.filter(([pk]) => pk !== myKey.publicKey);
      const list = [{ name: 'You', fp: await fingerprint(myKey.publicKey) }];
      for (const [pk, name] of people) list.push({ name, fp: await fingerprint(pk) });
      if (live) setRows(list);
    })().catch((e) => live && setError(errorText(e)));
    return () => { live = false; };
  }, [c, myKey, senders]);

  return (
    <div className="verify">
      <p className="hint">
        Compare these with each person in person or by phone. If they match, nobody (not even the server) can read
        or alter your messages.
      </p>
      {error && <p className="error">{error}</p>}
      {rows?.map((r) => (
        <div key={r.name + r.fp} className="fp-row"><span>{r.name}</span><code>{r.fp}</code></div>
      ))}
    </div>
  );
}

function NewConversation({ isOwner, onStarted, onCancel }: {
  isOwner: boolean; onStarted: (c: ChatConversation) => Promise<void>; onCancel: () => void;
}) {
  const [contacts, setContacts] = useState<ChatContact[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [title, setTitle] = useState('');
  const [filter, setFilter] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    chatApi.contacts().then(setContacts).catch((e) => setError(errorText(e)));
  }, []);

  const shown = contacts?.filter((c) => `${c.fullName} ${c.email}`.toLowerCase().includes(filter.toLowerCase())) ?? [];
  const toggle = (id: string) => setPicked((p) => {
    const n = new Set(p);
    if (n.has(id)) n.delete(id); else n.add(id);
    return n;
  });

  async function start(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await onStarted(await chatApi.createConversation([...picked], picked.size > 1 ? title.trim() : null));
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="new-conv" onSubmit={start}>
      <h2>New conversation</h2>
      <input type="search" placeholder="Search people" aria-label="Search people" value={filter} onChange={(e) => setFilter(e.target.value)} />
      {!contacts && !error && <p className="hint">Loading…</p>}
      {contacts?.length === 0 && <p className="hint">There is nobody you can start a conversation with yet.</p>}
      {!isOwner && contacts && (
        <p className="hint">
          People in more senior roles are listed only if you have the Message Senior Roles right. You can always reply
          when they message you.
        </p>
      )}
      <ul className="people">
        {shown.map((c) => (
          <li key={c.userId}>
            <label>
              <input type="checkbox" checked={picked.has(c.userId)} onChange={() => toggle(c.userId)} />
              <span>{c.fullName}</span>
              <span className={`role role-${c.role.toLowerCase()}`}>{c.role}</span>
              {!c.keyId && <span className="badge" title="They can read messages sent after they set up secure messaging.">not set up</span>}
            </label>
          </li>
        ))}
      </ul>
      {picked.size > 1 && (
        <label>Group name
          <input value={title} maxLength={100} onChange={(e) => setTitle(e.target.value)} required />
        </label>
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="actions">
        <button type="submit" disabled={busy || picked.size === 0}>
          {picked.size > 1 ? 'Create group' : 'Start chat'}
        </button>
        <button type="button" className="link" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

function SecurityPanel({ myKey, bundle, onBundle, onLock }: {
  myKey: UnlockedKey; bundle: ChatKeyBundle; onBundle: (b: ChatKeyBundle) => void; onLock: () => void;
}) {
  const [fp, setFp] = useState('');
  const [oldPass, setOldPass] = useState('');
  const [newPass, setNewPass] = useState('');
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { void fingerprint(myKey.publicKey).then(setFp).catch(() => undefined); }, [myKey]);

  async function change(e: FormEvent) {
    e.preventDefault();
    if (newPass.length < MIN_PASSPHRASE) return setStatus(`Use at least ${MIN_PASSPHRASE} characters.`);
    setBusy(true);
    try {
      const b = await changePassphrase(bundle, oldPass, newPass);
      onBundle(await chatApi.updateBackup({ encryptedPrivateKey: b.encryptedPrivateKey, kdfSalt: b.kdfSalt, kdfIterations: b.kdfIterations }));
      setOldPass('');
      setNewPass('');
      setStatus('Passphrase changed.');
    } catch (err) {
      setStatus(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <details className="security">
      <summary>Security</summary>
      <p className="hint">Your key fingerprint</p>
      <code className="fp">{fp}</code>
      <form onSubmit={change}>
        <input type="password" placeholder="Current passphrase" aria-label="Current passphrase" autoComplete="current-password"
               value={oldPass} onChange={(e) => setOldPass(e.target.value)} required />
        <input type="password" placeholder="New passphrase" aria-label="New passphrase" autoComplete="new-password"
               value={newPass} onChange={(e) => setNewPass(e.target.value)} required />
        <button type="submit" disabled={busy}>Change passphrase</button>
      </form>
      {status && <p className="hint" role="status">{status}</p>}
      <button type="button" className="link" onClick={onLock}>Lock messages on this device</button>
    </details>
  );
}
