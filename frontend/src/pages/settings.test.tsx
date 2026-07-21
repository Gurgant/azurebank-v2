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

  // EXACT counts — both responsive variants are in the jsdom DOM (media queries
  // hide one visually): name = mobile profile + desktop profile card; email = those
  // two PLUS the read-only Email form value; initials = the two avatars.
  const initials = `${MOCK_USER.firstName.charAt(0)}${MOCK_USER.lastName.charAt(0)}`.toUpperCase();
  expect(screen.getAllByText(`${MOCK_USER.firstName} ${MOCK_USER.lastName}`)).toHaveLength(2);
  expect(screen.getAllByText(MOCK_USER.email)).toHaveLength(3);
  expect(screen.getAllByText(initials)).toHaveLength(2);
  expect(screen.queryByText(/john doe/i)).not.toBeInTheDocument();
  expect(screen.queryByText('john.doe@email.com')).not.toBeInTheDocument();
});
