import { screen } from '@testing-library/react';
import { expect, it } from 'vitest';
import { MOCK_USER, seedMockSession } from '../mocks/state';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import { apiSlice } from '../features/api/apiSlice';
import { SettingsPage } from './SettingsPage';

/**
 * Regression pin: the settings profile identity must be the SESSION user — the shell
 * shows the same user right next to this content, and the two must never disagree
 * on one screen. (The remaining fabricated fields die in the settings rewrite.)
 */
it('settings profile shows the session user identity, never the old mock', async () => {
  seedMockSession();
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();

  renderWithProviders(<SettingsPage />, { store, routerEntries: ['/settings'] });

  // Both the mobile and desktop profile sections render the session identity.
  expect(
    screen.getAllByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`).length,
  ).toBeGreaterThan(0);
  expect(screen.getAllByText(MOCK_USER.email).length).toBeGreaterThan(0);
  expect(screen.queryByText(/john doe/i)).not.toBeInTheDocument();
  expect(screen.queryByText('john.doe@email.com')).not.toBeInTheDocument();
});
