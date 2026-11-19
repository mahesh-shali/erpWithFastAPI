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

  it('shows server error on bad credentials', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(jsonResponse({ title: 'Invalid email or password.' }, 401));
    renderWithProviders(<AppRoutes />, '/login');

    await userEvent.type(screen.getByLabelText(/email/i), 'admin@demo.com');
    await userEvent.type(screen.getByLabelText(/password/i), 'nope');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Invalid email or password.'));
    expect(fakeState.user).toBeNull();
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
