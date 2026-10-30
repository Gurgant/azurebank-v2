import { screen, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AuthBrandingPanel } from '../components/layout/AuthBrandingPanel';
import { renderWithProviders } from '../test/renderWithProviders';
import { DashboardPage } from './DashboardPage';

/**
 * Two small lines on a brand ground, white at full strength and nothing less: the sign-in panel's
 * copyright and the second line of Home's Transfer banner. An opacity on either took it under
 * 4.5:1 (3.84:1 and 3.97:1, in a browser). jsdom paints nothing, so a ratio cannot be read here;
 * the opacity that lowered it can, because Griffel inserts its rules in jsdom too.
 *
 * jsdom answers an empty string for an opacity that nothing declares, and `1` for one set to 1.
 */
const FULL_STRENGTH = ['', '1'];

describe('the lines that have to reach 4.5:1 on a brand ground', () => {
  it('draws the copyright on the sign-in panel at full strength', () => {
    renderWithProviders(<AuthBrandingPanel headline="Headline" intro="Intro" />);

    const copyright = screen.getByText(/All rights reserved/);

    expect(FULL_STRENGTH).toContain(getComputedStyle(copyright).opacity);
  });

  it("draws the second line of Home's Transfer banner at full strength", async () => {
    renderWithProviders(
      <Routes>
        <Route path="/" element={<DashboardPage />} />
      </Routes>,
      { routerEntries: ['/'] },
    );

    const banner = await screen.findByRole('button', { name: /^Transfer .+/ });
    const secondLine = within(banner).getByText(/^(Send to any|Recently sent to)/);

    expect(FULL_STRENGTH).toContain(getComputedStyle(secondLine).opacity);
  });
});
