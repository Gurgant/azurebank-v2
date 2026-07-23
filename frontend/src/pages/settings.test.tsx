import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { MOCK_USER, seedMockSession } from '../mocks/state';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import { apiSlice } from '../features/api/apiSlice';
import { SettingsPage } from './SettingsPage';

// Seed the auth slice directly (the wire shape authSlice keys on), so there is no live getMe
// subscription re-rendering the page mid-interaction. seedMockSession() keeps the MSW /me + rename
// handlers consistent for the explicit refetch the success case performs.
function seededStore() {
  seedMockSession();
  const store = makeTestStore();
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: { user: { ...MOCK_USER } },
  });
  return store;
}

async function renderSettings() {
  const store = seededStore();
  const utils = renderWithProviders(<SettingsPage />, { store, routerEntries: ['/settings'] });
  await screen.findByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`);
  return utils; // renderWithProviders returns the passed store
}

describe('settings page', () => {
  it('shows the session identity + @handle, and none of the fabricated PII', async () => {
    await renderSettings();
    const initials =
      `${MOCK_USER.firstName.charAt(0)}${MOCK_USER.lastName.charAt(0)}`.toUpperCase();

    expect(screen.getByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`)).toBeInTheDocument();
    expect(screen.getAllByText(MOCK_USER.email)).toHaveLength(2); // profile header + email field
    expect(screen.getByText(initials)).toBeInTheDocument();
    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument();

    // The old mockUser fiction must be gone.
    expect(screen.queryByText(/\+1 \(555\)/)).not.toBeInTheDocument();
    expect(screen.queryByText(/United States/)).not.toBeInTheDocument();
    expect(screen.queryByText(/March 15, 1990/)).not.toBeInTheDocument();
  });

  it('lists future settings as honest "Coming soon" rows', async () => {
    await renderSettings();
    expect(screen.getByText('Password & two-factor')).toBeInTheDocument();
    expect(screen.getAllByText('Coming soon').length).toBeGreaterThanOrEqual(5);
  });

  it('renames the public handle; a getMe refetch reflects the new one', async () => {
    const user = userEvent.setup();
    const { store } = await renderSettings();

    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Change' }));

    const input = await screen.findByRole('textbox');
    fireEvent.change(input, { target: { value: 'newtag' } });
    await user.click(await screen.findByRole('button', { name: 'Save' }));

    // On success the dialog closes; the app propagates the new tag via the Session invalidation.
    // Here we drive that refetch explicitly (no live subscription) — the MSW handler updated the
    // session, so /me now returns the new handle.
    await waitFor(() => expect(screen.queryByRole('textbox')).not.toBeInTheDocument());
    await store.dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));

    expect(store.getState().auth.user?.azureTag).toBe('newtag');
    expect(await screen.findByText('@newtag')).toBeInTheDocument();
  });

  it('surfaces a taken handle and keeps the old one', async () => {
    const user = userEvent.setup();
    await renderSettings();

    await user.click(screen.getByRole('button', { name: 'Change' }));
    const input = await screen.findByRole('textbox');
    fireEvent.change(input, { target: { value: 'friend' } }); // a seeded recipient → 409
    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByText('That handle is already taken.')).toBeInTheDocument();
    expect(screen.getByText(`@${MOCK_USER.azureTag}`)).toBeInTheDocument(); // handle unchanged
  });
});
