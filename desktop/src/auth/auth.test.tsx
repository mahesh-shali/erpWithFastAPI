import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { LoginPage } from '../pages/LoginPage';
import { demoUser, jsonResponse, renderWithProviders, signIn } from '../test/utils';
import { PrivateRoute } from './PrivateRoute';
import { fakeState } from '../test/fakeBridge';

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
  it('logs in through the main process and navigates home', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      jsonResponse({ accessToken: 'jwt-123', expiresAt: '2030-01-01', user: demoUser }));
    renderWithProviders(<AppRoutes />, '/login');

    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'Admin@123');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
    expect(fakeState.user).toEqual(demoUser);
  });

  it('shows the password on request and hides it again', async () => {
    renderWithProviders(<AppRoutes />, '/login');
    const field = screen.getByLabelText('Password');
    await userEvent.type(field, 'Admin@123');
    expect(field).toHaveAttribute('type', 'password');

    const toggle = screen.getByRole('button', { name: 'Show' });
    expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(toggle);
    expect(field).toHaveAttribute('type', 'text');
    expect(field).toHaveValue('Admin@123');
    expect(screen.getByRole('button', { name: 'Hide' })).toHaveAttribute('aria-pressed', 'true');

    await userEvent.click(screen.getByRole('button', { name: 'Hide' }));
    expect(field).toHaveAttribute('type', 'password');
    expect(screen.getAllByLabelText(/password/i)).toEqual([field]); // the toggle never matches a password lookup
  });

  it('shows server error on bad credentials', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ title: 'Invalid email or password.' }, 401));
    renderWithProviders(<AppRoutes />, '/login');

    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'nope');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Invalid email or password.'));
    expect(fakeState.user).toBeNull();
  });

  it('asks for the authenticator code when two-factor sign-in is on, then signs in with it', async () => {
    const codes: (string | undefined)[] = [];
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_url, init) => {
      const body = JSON.parse(String(init?.body));
      codes.push(body.code);
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
    expect(fakeState.user).toBeNull();

    await userEvent.type(code, '000000');
    await userEvent.click(screen.getByRole('button', { name: /verify/i }));
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Invalid verification code.'));

    await userEvent.type(screen.getByLabelText(/verification code/i), '123456');
    await userEvent.click(screen.getByRole('button', { name: /verify/i }));
    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
    expect(fakeState.user).toEqual(demoUser);
    expect(codes).toEqual([undefined, '000000', '123456']);
  });
});

describe('session restore at startup', () => {
  it('shows a splash, then resumes the stored session from the main process', async () => {
    const { render } = await import('@testing-library/react');
    const { MemoryRouter } = await import('react-router-dom');
    const { AuthProvider } = await import('./AuthContext');
    fakeState.user = demoUser;
    render(
      <MemoryRouter initialEntries={['/']}><AuthProvider><AppRoutes /></AuthProvider></MemoryRouter>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('Opening where you left off…');
    expect(await screen.findByRole('heading', { name: 'Home' })).toBeInTheDocument();
  });

  it('goes to login when there is no session to restore', async () => {
    const { render } = await import('@testing-library/react');
    const { MemoryRouter } = await import('react-router-dom');
    const { AuthProvider } = await import('./AuthContext');
    render(
      <MemoryRouter initialEntries={['/']}><AuthProvider><AppRoutes /></AuthProvider></MemoryRouter>,
    );
    expect(await screen.findByRole('form', { name: /login form/i })).toBeInTheDocument();
  });
});
