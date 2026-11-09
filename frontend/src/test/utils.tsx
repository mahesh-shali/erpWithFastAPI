import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../auth/AuthContext';
import { TOKEN_KEY, type UserDto } from '../api/client';

export const demoUser: UserDto = {
  id: 'u1', email: 'admin@demo.com', fullName: 'Demo Admin', role: 'Admin',
  organizationId: '11111111-1111-1111-1111-111111111111',
};

export function signIn(user: UserDto = demoUser) {
  localStorage.setItem(TOKEN_KEY, 'test-token');
  localStorage.setItem('erp.user', JSON.stringify(user));
}

export function renderWithProviders(ui: ReactElement, route = '/') {
  return render(
    <MemoryRouter initialEntries={[route]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  );
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}
