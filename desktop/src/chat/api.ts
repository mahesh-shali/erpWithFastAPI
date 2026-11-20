import { request } from '../api/client';
import type { KeyBackup, SealedMessage } from './crypto';

/** Same in the web and desktop apps: `request` is the app's own API client. */

export interface ChatKeyBundle extends KeyBackup { keyId: string; fingerprint: string; createdAt: string }
/** keyId null = this person has not set up secure messaging yet. */
export interface ChatContact {
  userId: string; fullName: string; email: string; role: string;
  keyId: string | null; publicKey: string | null; fingerprint: string | null;
}
export interface ChatMember { userId: string; fullName: string; role: string; fingerprint: string | null }
export type ChatKind = 'direct' | 'group' | 'announcement';
export interface ChatConversation {
  id: string; kind: ChatKind; title: string | null; members: ChatMember[]; lastMessageAt: string;
  unread: number; lastReadMessageId: number; canPost: boolean;
}
export interface ChatRecipients { recipients: ChatContact[]; withoutKey: string[] }
export interface ChatMessageDto {
  id: number; conversationId: string; senderId: string | null; senderName: string; senderKeyId: string;
  senderPublicKey: string; ciphertext: string; iv: string; wrappedKey: string | null; createdAt: string;
}
/** Live notice from the server (never content). ready/reconnected come from the transport itself. */
export interface ChatEvent {
  type: 'message' | 'conversation' | 'ready' | 'reconnected';
  conversationId?: string | null; messageId?: number | null;
}

const json = (body: unknown) => JSON.stringify(body);
const conv = (id: string) => `/api/chat/conversations/${encodeURIComponent(id)}`;

export const chatApi = {
  /** undefined (204) = not set up yet. */
  myKey: () => request<ChatKeyBundle | undefined>('/api/chat/keys/me'),
  registerKey: (b: KeyBackup) => request<ChatKeyBundle>('/api/chat/keys', { method: 'POST', body: json(b) }),
  resetKey: (b: KeyBackup) => request<ChatKeyBundle>('/api/chat/keys/reset', { method: 'POST', body: json(b) }),
  updateBackup: (b: Omit<KeyBackup, 'publicKey'>) =>
    request<ChatKeyBundle>('/api/chat/keys/me/backup', { method: 'PUT', body: json(b) }),

  contacts: () => request<ChatContact[]>('/api/chat/contacts'),
  conversations: () => request<ChatConversation[]>('/api/chat/conversations'),
  createConversation: (memberIds: string[], title: string | null) =>
    request<ChatConversation>('/api/chat/conversations', { method: 'POST', body: json({ memberIds, title }) }),
  recipients: (id: string) => request<ChatRecipients>(`${conv(id)}/recipients`),

  messages: (id: string, f: { before?: number; after?: number } = {}) => {
    const q = f.after !== undefined ? `?after=${f.after}` : f.before !== undefined ? `?before=${f.before}` : '';
    return request<ChatMessageDto[]>(`${conv(id)}/messages${q}`);
  },
  send: (id: string, senderKeyId: string, sealed: SealedMessage) =>
    request<ChatMessageDto>(`${conv(id)}/messages`, { method: 'POST', body: json({ senderKeyId, ...sealed }) }),
  markRead: (id: string, messageId: number) =>
    request<void>(`${conv(id)}/read`, { method: 'POST', body: json({ messageId }) }),
};
