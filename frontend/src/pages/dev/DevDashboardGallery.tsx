import { Link, useParams } from 'react-router-dom';
import { makeStyles, Text, tokens } from '@fluentui/react-components';
import { colors, surfaces } from '../../theme/tokens';
import { DirectionA } from './directions/DirectionA';
import { DirectionB } from './directions/DirectionB';
import { DirectionC } from './directions/DirectionC';
import { DirectionD } from './directions/DirectionD';

/**
 * SCRATCH. Deleted in the same PR that picks a winner — U3 is the programme's one generative step
 * and this is its scaffolding, not a feature.
 *
 * Three dashboard directions, each in its own file, rendered inside the REAL app shell. The shell
 * is not a detail here: the defect this phase inherits is that the two-column layout turns on at
 * 1024px while needing 1095px, and 240px of that is the shell's own sidebar. A gallery that
 * rendered these full-bleed would hide the exact thing they have to solve.
 *
 * **The route is gated on `import.meta.env.DEV` in `App.tsx` and the gate is verified, not
 * assumed** — a production build is grepped for `U3_SCRATCH_MARKER` below, and a hit means the gate
 * leaked. This repo has form here: `DEV_BYPASS_AUTH` was deleted in A3 precisely because a dev-only
 * escape hatch that nobody re-checks stops being dev-only.
 */

/** Grepped for in `dist/` after a production build. Must NOT appear there. */
export const U3_SCRATCH_MARKER = 'U3-SCRATCH-DASHBOARD-DIRECTIONS';

const DIRECTIONS = [
  { key: 'a', label: 'A — the balance is the page', Component: DirectionA },
  { key: 'b', label: 'B — the page is a launchpad', Component: DirectionB },
  { key: 'c', label: 'C — the page is a ledger', Component: DirectionC },
  { key: 'd', label: 'D — hybrid', Component: DirectionD },
] as const;

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
    padding: '8px 16px',
    backgroundColor: colors.neutral[800],
    // Scratch chrome, deliberately ugly so it can never be mistaken for the design under review.
    color: tokens.colorNeutralForegroundOnBrand,
    fontSize: '13px',
  },
  link: {
    padding: '4px 10px',
    borderRadius: '6px',
    color: tokens.colorNeutralForegroundOnBrand,
    textDecoration: 'none',
    border: `1px solid ${surfaces.border}`,
  },
  active: {
    backgroundColor: colors.brand[60],
    fontWeight: 600,
  },
});

export function DevDashboardGallery() {
  const styles = useStyles();
  const { variant } = useParams();
  const active = DIRECTIONS.find((d) => d.key === variant) ?? DIRECTIONS[0];
  const { Component } = active;

  return (
    <>
      <div className={styles.bar}>
        <Text style={{ fontWeight: 700 }}>U3 scratch</Text>
        {DIRECTIONS.map((d) => (
          <Link
            key={d.key}
            to={`/dev/dashboard/${d.key}`}
            className={`${styles.link} ${d.key === active.key ? styles.active : ''}`}
          >
            {d.label}
          </Link>
        ))}
      </div>
      <Component />
    </>
  );
}

export default DevDashboardGallery;
