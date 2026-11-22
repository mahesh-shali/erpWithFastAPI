import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { LoginPage } from '../pages/LoginPage';
import { demoUser, jsonResponse, renderWithProviders, signIn } from '../test/utils';
import { PrivateRoute } from './PrivateRoute';
import { TOKEN_KEY } from '../api/client';

function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/" element={<PrivateRoute><h1>Home</h1></PrivateRoute>} />
      <Route path="/admin" element={<PrivateRoute role="Admin"><h1>Admin area</h1></PrivateRoute>} />
    </Routes>
  );
}

describe('PrivateRoute', () => {
  it('redirects anonymous users to /login', () => {
    renderWithProviders(<AppRoutes />, '/');
    expect(screen.getByRole('form', { name: /login form/i })).toBeInTheDocument();
  });

  it('renders protected content when signed in', () => {
    signIn();
    renderWithProviders(<AppRoutes />, '/');
    expect(screen.getByRole('heading', { name: 'Home' })).toBeInTheDocument();
  });

  it('blocks non-admins from admin routes', () => {
    signIn({ ...demoUser, role: 'User' });
    renderWithProviders(<AppRoutes />, '/admin');
    expect(screen.getByRole('heading', { name: 'Home' })).toBeInTheDocument();
  });
});

describe('LoginPage', () => {
  it('logs in, stores token and navigates home', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      jsonResponse({ accessToken: 'jwt-123', expiresAt: '2030-01-01', user: demoUser }));
    renderWithProviders(<AppRoutes />, '/login');

    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
    expect(localStorage.getItem(TOKEN_KEY)).toBe('jwt-123');
  });

  it('shows server error on bad credentials', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ title: 'Invalid email or password.' }, 401));
    renderWithProviders(<AppRoutes />, '/login');

    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'nope');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Invalid email or password.'));
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
  });

  it('asks for the authenticator code when two-factor sign-in is on, then signs in with it', async () => {
    const bodies: { code?: string }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_url, init) => {
      const body = JSON.parse(String(init?.body));
      bodies.push(body);
      if (!body.code) return jsonResponse({ twoFactorRequired: true });
      return body.code === '123456'
        ? jsonResponse({ accessToken: 'jwt-2fa', expiresAt: '2030-01-01', user: demoUser })
        : jsonResponse({ title: 'Invalid verification code.' }, 401);
    });
    renderWithProviders(<AppRoutes />, '/login');

    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    const code = await screen.findByLabelText(/verification code/i);
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull(); // no session after the password alone

    await userEvent.type(code, '000000');
    await userEvent.click(screen.getByRole('button', { name: /verify/i }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Invalid verification code.'));

    await userEvent.type(screen.getByLabelText(/verification code/i), '123456');
    await userEvent.click(screen.getByRole('button', { name: /verify/i }));
    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
    expect(localStorage.getItem(TOKEN_KEY)).toBe('jwt-2fa');
    expect(bodies.map((b) => b.code)).toEqual([undefined, '000000', '123456']);
  });

  it('"Remember this device" is sent with the code, never with the password alone', async () => {
    const bodies: { code?: string; rememberDevice?: boolean }[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_url, init) => {
      const body = JSON.parse(String(init?.body));
      bodies.push(body);
      return body.code ? jsonResponse({ accessToken: 'jwt', expiresAt: '2030-01-01', user: demoUser }) : jsonResponse({ twoFactorRequired: true });
    });
    renderWithProviders(<AppRoutes />, '/login');
    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    const remember = await screen.findByRole('checkbox', { name: /remember this device for 30 days/i });
    expect(remember).not.toBeChecked();
    await userEvent.click(remember);
    await userEvent.type(screen.getByLabelText(/verification code/i), '123456');
    await userEvent.click(screen.getByRole('button', { name: /verify/i }));
    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
    expect(bodies.map((b) => b.rememberDevice)).toEqual([false, true]);
  });

  it('can go back from the code step to the password form', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ twoFactorRequired: true }));
    renderWithProviders(<AppRoutes />, '/login');
    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));
    await userEvent.click(await screen.findByRole('button', { name: /back to sign in/i }));
    expect(screen.getByRole('form', { name: /login form/i })).toBeInTheDocument();
    expect(screen.getByLabelText(/password/i)).toHaveValue('');
  });
});
