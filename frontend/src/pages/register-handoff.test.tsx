import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { advance, fakeClockUser, installFakeClock } from '../test/outage';
import { renderWithProviders } from '../test/renderWithProviders';
import { RegisterPage } from './RegisterPage';

function RegisterHarness() {
  return (
    <Routes>
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/pin-setup" element={<div>PIN SETUP PAGE</div>} />
      <Route path="/dashboard" element={<div>DASHBOARD PAGE</div>} />
    </Routes>
  );
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>, email: string) {
  await user.type(screen.getByLabelText(/first name/i), 'Test');
  await user.type(screen.getByLabelText(/last name/i), 'User');
  await user.type(screen.getByLabelText(/azuretag/i), 'test_user');
  await user.type(screen.getByLabelText(/^email address$/i), email);
  await user.type(screen.getByLabelText(/^password$/i), 'Password1!');
  await user.type(screen.getByLabelText(/^confirm password$/i), 'Password1!');
  await user.click(screen.getByRole('button', { name: /create account/i }));
}

describe('register → PIN setup handoff', () => {
  it('sends a brand-new (PIN-less) user to /pin-setup with returnTo=/dashboard', async () => {
    const user = userEvent.setup();
    const { router } = renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });

    // The default MSW register handler returns a fresh user with hasPin:false.
    await fillAndSubmit(user, 'fresh@azurebank.dev');

    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
    expect(router.state.location.pathname + router.state.location.search).toBe(
      '/pin-setup?returnTo=/dashboard',
    );
    expect(screen.queryByText('DASHBOARD PAGE')).not.toBeInTheDocument();
  });

  it('sends an already-PIN’d registration straight to the dashboard', async () => {
    // The other branch of the handoff: a 201 whose user already has a PIN skips the wizard.
    server.use(
      http.post('*/bff/auth/register', () =>
        HttpResponse.json(
          {
            data: {
              user: {
                id: '019f7b3f-0000-7000-8000-00000000aaaa',
                email: 'pinned@azurebank.dev',
                firstName: 'P',
                lastName: 'U',
                azureTag: 'pinned_user',
                hasPin: true,
              },
              // Far-future + fixed (the repo's mocks avoid Date.now()): a successful-registration
              // fixture must not ship a pre-expired session.
              expiresAt: '2099-01-01T00:00:00.0000000Z',
            },
            message: 'ok',
          },
          { status: 201 },
        ),
      ),
    );
    const user = userEvent.setup();
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });

    await fillAndSubmit(user, 'pinned@azurebank.dev');

    expect(await screen.findByText('DASHBOARD PAGE')).toBeInTheDocument();
    expect(screen.queryByText('PIN SETUP PAGE')).not.toBeInTheDocument();
  });
});

describe('register: the two password fields', () => {
  it('gives each reveal toggle a name of its own', async () => {
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });

    // Both used to be "Show password", so a list of the page's buttons held two identical entries.
    expect(await screen.findByRole('button', { name: 'Show password' })).toBeInTheDocument();
    const confirm = screen.getByRole('button', { name: 'Show confirm password' });
    await userEvent.click(confirm);
    expect(screen.getByRole('button', { name: 'Hide confirm password' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show password' })).toBeInTheDocument();
  });
});

/*
  What the form does with a refusal that names fields. The server answers a rule it enforces and
  the form does not with a 400 and an `errors` dictionary, keyed by the field in PascalCase
  (model state) or camelCase (FluentValidation). The rules below are made up for the test, in
  that envelope: what is held is where each sentence lands, and that none is swallowed.
*/
describe('register: a refusal that names fields', () => {
  /** The form filled in with details the form itself accepts; nothing is sent yet. */
  async function fill(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText(/first name/i), 'Test');
    await user.type(screen.getByLabelText(/last name/i), 'User');
    await user.type(screen.getByLabelText(/azuretag/i), 'test_user');
    await user.type(screen.getByLabelText(/^email address$/i), 'fresh@azurebank.dev');
    await user.type(screen.getByLabelText(/^password$/i), 'Password1!');
    await user.type(screen.getByLabelText(/^confirm password$/i), 'Password1!');
  }

  it("puts the server's sentence on the field it names, in either spelling of the key", async () => {
    server.use(
      http.post('*/bff/auth/register', () =>
        problem({
          status: 400,
          errors: {
            Email: ['This email domain is not accepted.', 'A second sentence that is not shown.'],
            azureTag: ['This handle is reserved.'],
            // A key with no sentence says nothing, and breaks nothing.
            Password: [],
          },
        }),
      ),
    );
    const user = userEvent.setup();
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });
    await fill(user);
    await user.click(screen.getByRole('button', { name: /create account/i }));

    expect(await screen.findByText('This email domain is not accepted.')).toBeVisible();
    expect(screen.getByText('This handle is reserved.')).toBeVisible();
    expect(screen.queryByText('A second sentence that is not shown.')).not.toBeInTheDocument();
    expect({
      email: screen.getByLabelText(/^email address$/i).getAttribute('aria-invalid'),
      azureTag: screen.getByLabelText(/azuretag/i).getAttribute('aria-invalid'),
      password: screen.getByLabelText(/^password$/i).getAttribute('aria-invalid'),
      firstName: screen.getByLabelText(/first name/i).getAttribute('aria-invalid'),
    }).toEqual({ email: 'true', azureTag: 'true', password: 'false', firstName: 'false' });
    // What was typed is kept: the visitor corrects, and does not start again.
    expect(screen.getByLabelText(/^email address$/i)).toHaveValue('fresh@azurebank.dev');
    expect(screen.queryByText('PIN SETUP PAGE')).not.toBeInTheDocument();
  });

  it('says a rule about a field the form does not have, in an alert, and never submits into silence', async () => {
    server.use(
      http.post('*/bff/auth/register', () =>
        problem({ status: 400, errors: { DateOfBirth: ['You must be 18 or older.'] } }),
      ),
    );
    const user = userEvent.setup();
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });
    await fill(user);
    await user.click(screen.getByRole('button', { name: /create account/i }));

    const words = await screen.findByText('You must be 18 or older.');
    expect(words.closest('[role="alert"]')).not.toBeNull();
    expect(screen.queryByText('PIN SETUP PAGE')).not.toBeInTheDocument();
  });

  it('the first password field has a reveal of its own', async () => {
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });
    const password = await screen.findByLabelText(/^password$/i);
    expect(password).toHaveAttribute('type', 'password');

    await userEvent.click(screen.getByRole('button', { name: 'Show password' }));

    expect(password).toHaveAttribute('type', 'text');
    expect(screen.getByRole('button', { name: 'Hide password' })).toBeInTheDocument();
    // The confirmation is not shown with it.
    expect(screen.getByLabelText(/^confirm password$/i)).toHaveAttribute('type', 'password');
  });
});

describe('register: the limiter lets go', () => {
  // Unmount first, then restore the clock: a countdown still mounted on a fake clock would go on
  // ticking into the next test.
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('takes the countdown away and gives the button back when the wait is over', async () => {
    // The BFF's limiter, shared with sign-in: 429 RATE_LIMIT_EXCEEDED with `retryAfterSeconds`,
    // from which the browser counts down by itself (CONVENTIONS.md). Five seconds here.
    server.use(
      http.post('*/bff/auth/register', () =>
        problem({
          status: 429,
          errorCode: 'RATE_LIMIT_EXCEEDED',
          detail: 'Too many requests. Please retry later.',
          extensions: { retryAfterSeconds: 5 },
        }),
      ),
    );
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });
    const typing = userEvent.setup();
    await typing.type(screen.getByLabelText(/first name/i), 'Test');
    await typing.type(screen.getByLabelText(/last name/i), 'User');
    await typing.type(screen.getByLabelText(/azuretag/i), 'test_user');
    await typing.type(screen.getByLabelText(/^email address$/i), 'fresh@azurebank.dev');
    await typing.type(screen.getByLabelText(/^password$/i), 'Password1!');
    await typing.type(screen.getByLabelText(/^confirm password$/i), 'Password1!');

    // Before the press: until then no countdown exists, so nothing of it is on the real clock.
    installFakeClock();
    await fakeClockUser().click(screen.getByRole('button', { name: /create account/i }));
    expect(await screen.findByRole('timer')).toHaveTextContent(/try again in/i);
    expect(screen.getByText(/Too many attempts from your connection\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /create account/i })).toBeDisabled();

    await advance(6_000);

    await waitFor(() => expect(screen.queryByRole('timer')).not.toBeInTheDocument());
    expect(screen.queryByText(/Too many attempts from your connection\./)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /create account/i })).toBeEnabled();
  });
});
