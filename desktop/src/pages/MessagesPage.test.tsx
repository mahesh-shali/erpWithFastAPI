import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { UserDto } from '../api/client';
import type { ChatConversation, ChatMessageDto } from '../chat/api';
import { forgetDeviceKeys, saveDeviceKey } from '../chat/deviceKeys';
import { calls, jsonResponse, mockApi, renderWithProviders, signIn, type Route } from '../test/utils';
import { MessagesPage } from './MessagesPage';

// Real crypto is covered by chat/crypto.test.ts; here "ciphertext" is just base64 of the text.
vi.mock('../chat/crypto', () => {
  class WrongPassphraseError extends Error { constructor() { super('That passphrase is not correct.'); } }
  return {
    WrongPassphraseError,
    createIdentity: vi.fn(async () => ({
      backup: { publicKey: 'PUB-NEW', encryptedPrivateKey: 'ENC', kdfSalt: 'SALT', kdfIterations: 600000 },
      privateKey: { type: 'private' },
    })),
    unlockIdentity: vi.fn(async (_b: unknown, pass: string) => {
      if (pass !== 'right passphrase') throw new WrongPassphraseError();
      return { type: 'private' };
    }),
    encryptMessage: vi.fn(async (_me: unknown, _c: string, p: { text: string }, recipients: { keyId: string }[]) => ({
      ciphertext: btoa(p.text), iv: 'IV', keys: recipients.map((r) => ({ recipientKeyId: r.keyId, wrappedKey: `W-${r.keyId}` })),
    })),
    decryptMessage: vi.fn(async (_me: unknown, m: { ciphertext: string; wrappedKey: string | null }) => {
      if (!m.wrappedKey) throw new Error('Sent before your current key was set up, so it can no longer be read.');
      return { text: atob(m.ciphertext) };
    }),
    fingerprint: vi.fn(async (pk: string) => `fp-${pk}`),
    changePassphrase: vi.fn(),
  };
});
const live = vi.hoisted(() => ({ listener: null as null | ((e: { type: string; conversationId?: string }) => void) }));
vi.mock('../chat/events', () => ({
  subscribeChatEvents: (fn: (e: { type: string }) => void) => {
    live.listener = fn;
    return () => { live.listener = null; };
  },
}));

const member: UserDto = { id: 'u-a', email: 'a@x.com', fullName: 'Alice Admin', role: 'Admin', organizationId: 'o1' };
const owner: UserDto = { id: 'u-o', email: 'o@x.com', fullName: 'Olivia Owner', role: 'SuperAdmin', organizationId: 'o1' };
const bundle = (userId: string) => ({
  keyId: `k-${userId}`, publicKey: `PUB-${userId}`, fingerprint: 'f', encryptedPrivateKey: 'E', kdfSalt: 'S', kdfIterations: 600000, createdAt: '2026-11-20T10:00:00Z',
});
const announcement: ChatConversation = {
  id: 'c-ann', kind: 'announcement', title: 'Announcements', members: [], lastMessageAt: '2026-11-20T10:00:00Z',
  unread: 1, lastReadMessageId: 0, canPost: false,
};
const direct: ChatConversation = {
  id: 'c-dm', kind: 'direct', title: null, lastMessageAt: '2026-11-20T11:00:00Z', unread: 0, lastReadMessageId: 0, canPost: true,
  members: [
    { userId: 'u-a', fullName: 'Alice Admin', role: 'Admin', fingerprint: 'x' },
    { userId: 'u-b', fullName: 'Bob User', role: 'User', fingerprint: 'y' },
  ],
};
const msg = (id: number, conversationId: string, senderId: string, senderName: string, text: string, wrapped = true): ChatMessageDto => ({
  id, conversationId, senderId, senderName, senderKeyId: `k-${senderId}`, senderPublicKey: `PUB-${senderId}`,
  ciphertext: btoa(text), iv: 'IV', wrappedKey: wrapped ? 'W' : null, createdAt: '2026-11-20T11:00:00Z',
});

function render(user: UserDto, routes: Route[]) {
  const spy = mockApi(routes);
  signIn(user);
  renderWithProviders(<MessagesPage />, '/messages');
  return spy;
}

async function unlockedOnThisDevice(user: UserDto) {
  const b = bundle(user.id);
  await saveDeviceKey({ userId: user.id, keyId: b.keyId, publicKey: b.publicKey, privateKey: {} as CryptoKey });
}

beforeEach(async () => {
  await forgetDeviceKeys();
});

describe('MessagesPage', () => {
  it('first visit: sets up a key with a passphrase, then shows the conversations', async () => {
    let registered: unknown;
    const spy = render(member, [
      ['GET', '/api/chat/keys/me', undefined],
      ['POST', '/api/chat/keys', (body: unknown) => { registered = body; return bundle(member.id); }],
      ['GET', '/api/chat/conversations', [announcement]],
    ]);
    const u = userEvent.setup();
    await screen.findByRole('heading', { name: 'Set up secure messaging' });

    await u.type(screen.getByLabelText('New passphrase'), 'correct horse 1');
    await u.type(screen.getByLabelText('Repeat passphrase'), 'correct horse 2');
    await u.click(screen.getByRole('button', { name: 'Set up' }));
    expect(screen.getByRole('alert')).toHaveTextContent('do not match');

    await u.clear(screen.getByLabelText('Repeat passphrase'));
    await u.type(screen.getByLabelText('Repeat passphrase'), 'correct horse 1');
    await u.click(screen.getByRole('button', { name: 'Set up' }));

    expect(await screen.findByRole('button', { name: /Announcements/ })).toBeInTheDocument();
    expect(registered).toEqual({ publicKey: 'PUB-NEW', encryptedPrivateKey: 'ENC', kdfSalt: 'SALT', kdfIterations: 600000 });
    expect(calls(spy)).toContain('POST /api/chat/keys');
  });

  it('on a new device the key must be unlocked with the passphrase', async () => {
    render(member, [
      ['GET', '/api/chat/keys/me', bundle(member.id)],
      ['GET', '/api/chat/conversations', [announcement]],
    ]);
    const u = userEvent.setup();
    await u.type(await screen.findByLabelText('Passphrase'), 'wrong');
    await u.click(screen.getByRole('button', { name: 'Unlock' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('not correct');

    await u.clear(screen.getByLabelText('Passphrase'));
    await u.type(screen.getByLabelText('Passphrase'), 'right passphrase');
    await u.click(screen.getByRole('button', { name: 'Unlock' }));
    expect(await screen.findByRole('button', { name: /Announcements/ })).toBeInTheDocument();
  });

  it('members read announcements but cannot post there', async () => {
    await unlockedOnThisDevice(member);
    const spy = render(member, [
      ['GET', '/api/chat/keys/me', bundle(member.id)],
      ['GET', '/api/chat/conversations', [announcement, direct]],
      ['GET', '/api/chat/conversations/c-ann/messages', [msg(5, 'c-ann', 'u-o', 'Olivia Owner', 'Office closed Friday')]],
      ['POST', '/api/chat/conversations/c-ann/read', undefined],
    ]);
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: /Announcements/ }));

    expect(await screen.findByText('Office closed Friday')).toBeInTheDocument();
    expect(screen.getByText('Only the organization owner can post here.')).toBeInTheDocument();
    expect(screen.queryByLabelText('Message')).not.toBeInTheDocument();
    await waitFor(() => expect(calls(spy)).toContain('POST /api/chat/conversations/c-ann/read'));
  });

  it('sends a message encrypted for every member key, retrying once when the keys changed', async () => {
    await unlockedOnThisDevice(member);
    let sends = 0;
    let sent: { senderKeyId: string; keys: { recipientKeyId: string }[]; ciphertext: string } | undefined;
    render(member, [
      ['GET', '/api/chat/keys/me', bundle(member.id)],
      ['GET', '/api/chat/conversations', [announcement, direct]],
      ['GET', '/api/chat/conversations/c-dm/messages*', [msg(1, 'c-dm', 'u-b', 'Bob User', 'Hi Alice'), msg(2, 'c-dm', 'u-b', 'Bob User', 'old', false)]],
      ['GET', '/api/chat/conversations/c-dm/recipients', {
        recipients: [
          { userId: 'u-a', fullName: 'Alice Admin', email: '', role: 'Admin', keyId: 'k-u-a', publicKey: 'PUB-u-a', fingerprint: 'f' },
          { userId: 'u-b', fullName: 'Bob User', email: '', role: 'User', keyId: 'k-u-b', publicKey: 'PUB-u-b', fingerprint: 'f' },
        ],
        withoutKey: [],
      }],
      ['POST', '/api/chat/conversations/c-dm/messages', (body: typeof sent) => {
        sends++;
        if (sends === 1) return jsonResponse({ title: 'The members of this conversation or their keys have changed.' }, 409);
        sent = body;
        return msg(3, 'c-dm', 'u-a', 'Alice Admin', 'Hello Bob');
      }],
      ['POST', '/api/chat/conversations/c-dm/read', undefined],
    ]);
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: /Bob User/ }));
    expect(await screen.findByText('Hi Alice')).toBeInTheDocument();
    expect(screen.getByText(/can no longer be read/)).toBeInTheDocument();

    await u.type(screen.getByLabelText('Message'), 'Hello Bob{Enter}');
    expect(await screen.findByText('Hello Bob')).toBeInTheDocument();
    expect(sends).toBe(2);
    expect(sent!.senderKeyId).toBe('k-u-a');
    expect(sent!.keys.map((k) => k.recipientKeyId).sort()).toEqual(['k-u-a', 'k-u-b']);
    expect(sent!.ciphertext).toBe(btoa('Hello Bob'));
  });

  it('the SuperAdmin posts announcements and can also start conversations', async () => {
    await unlockedOnThisDevice(owner);
    render(owner, [
      ['GET', '/api/chat/keys/me', bundle(owner.id)],
      ['GET', '/api/chat/conversations', [{ ...announcement, canPost: true, unread: 0 }]],
      ['GET', '/api/chat/conversations/c-ann/messages', []],
      ['POST', '/api/chat/conversations/c-ann/read', undefined],
    ]);
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: /Announcements/ }));
    expect(await screen.findByLabelText('Message')).toHaveAttribute('placeholder', 'Write an announcement…');
    expect(screen.getByRole('button', { name: 'New' })).toBeInTheDocument();
  });

  it('everyone can post in the General channel, listed right after Announcements', async () => {
    await unlockedOnThisDevice(member);
    const general: ChatConversation = { ...announcement, id: 'c-gen', kind: 'general', title: 'General', canPost: true, unread: 0 };
    render(member, [
      ['GET', '/api/chat/keys/me', bundle(member.id)],
      ['GET', '/api/chat/conversations', [direct, general, announcement]],
      ['GET', '/api/chat/conversations/c-gen/messages', [msg(7, 'c-gen', 'u-b', 'Bob User', 'Morning all')]],
      ['POST', '/api/chat/conversations/c-gen/read', undefined],
    ]);
    const u = userEvent.setup();
    const names = (await screen.findAllByRole('button', { name: /Announcements|General|Bob User/ })).map((b) => b.textContent);
    expect(names[0]).toMatch(/Announcements/);
    expect(names[1]).toMatch(/General/);
    await u.click(screen.getByRole('button', { name: /General/ }));
    expect(await screen.findByText('Morning all')).toBeInTheDocument();
    expect(screen.getByLabelText('Message')).toHaveAttribute('placeholder', 'Write a message…');
    expect(screen.getByText('Everyone in your organization receives these messages.')).toBeInTheDocument();
  });

  it('new messages announced on the live stream are fetched and shown', async () => {
    await unlockedOnThisDevice(member);
    let newest = [msg(1, 'c-dm', 'u-b', 'Bob User', 'first')];
    render(member, [
      ['GET', '/api/chat/keys/me', bundle(member.id)],
      ['GET', '/api/chat/conversations', [announcement, direct]],
      ['GET', '/api/chat/conversations/c-dm/messages?after=1', () => newest],
      ['GET', '/api/chat/conversations/c-dm/messages', [msg(1, 'c-dm', 'u-b', 'Bob User', 'first')]],
      ['POST', '/api/chat/conversations/c-dm/read', undefined],
    ]);
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: /Bob User/ }));
    await screen.findByText('first');

    newest = [msg(2, 'c-dm', 'u-b', 'Bob User', 'second')];
    live.listener!({ type: 'message', conversationId: 'c-dm' });
    expect(await screen.findByText('second')).toBeInTheDocument();
  });

  it('starts a group conversation with the chosen people', async () => {
    await unlockedOnThisDevice(member);
    let created: unknown;
    render(member, [
      ['GET', '/api/chat/keys/me', bundle(member.id)],
      ['GET', '/api/chat/conversations', [announcement]],
      ['GET', '/api/chat/contacts', [
        { userId: 'u-b', fullName: 'Bob User', email: 'b@x.com', role: 'User', keyId: 'k-u-b', publicKey: 'P', fingerprint: 'f' },
        { userId: 'u-c', fullName: 'Cara User', email: 'c@x.com', role: 'User', keyId: null, publicKey: null, fingerprint: null },
      ]],
      ['POST', '/api/chat/conversations', (body: unknown) => { created = body; return { ...direct, id: 'c-g', kind: 'group', title: 'Night shift' }; }],
      ['GET', '/api/chat/conversations/c-g/messages', []],
    ]);
    const u = userEvent.setup();
    await u.click(await screen.findByRole('button', { name: 'New' }));
    await u.click(await screen.findByRole('checkbox', { name: /Bob User/ }));
    await u.click(screen.getByRole('checkbox', { name: /Cara User/ }));
    expect(screen.getByText('not set up')).toBeInTheDocument();
    await u.type(screen.getByLabelText('Group name'), 'Night shift');
    await u.click(screen.getByRole('button', { name: 'Create group' }));
    await waitFor(() => expect(created).toEqual({ memberIds: ['u-b', 'u-c'], title: 'Night shift' }));
  });
});
