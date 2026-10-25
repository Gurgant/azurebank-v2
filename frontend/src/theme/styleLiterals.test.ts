import { readdirSync, readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

/**
 * A ratchet on the style literals outside `src/theme`: a size, weight, line height, corner or
 * spacing written as a number where a role or a Fluent token could stand. The count may fall and
 * may not rise, so a new literal fails here while the ones already written wait for their page.
 *
 * The patterns, the files read and the comment lines skipped are those of the count the design
 * system's analysis made with its script `count.py`, and the baseline below is that script's
 * count on bd743911: fontSize 156 lines, fontWeight 116, lineHeight 21, borderRadius 79 and
 * spacing 289. Each number is the lines in one file that match; a line counts once however many
 * literals it holds. Read: every `.ts` and `.tsx` under `src`, but tests, `src/test`,
 * `src/mocks`, `src/contract`, `src/integration`, `src/api/generated` and `src/theme`. Skipped:
 * a line that starts with `//`, `*` or `/*` once trimmed; a comment in JSX, inside its braces,
 * is counted.
 *
 * A change that removes literals may lower the baseline, and should: set each file it touched to
 * the lines that file now matches, and take out a file at zero. The name of each test below gives
 * the total now and the total allowed.
 */

const PATTERNS = {
  fontSize: /fontSize\s*:\s*['"`]?\d/,
  fontWeight: /fontWeight\s*:\s*['"`]?\d/,
  lineHeight: /lineHeight\s*:\s*['"`]?\d/,
  borderRadius: /(borderRadius|border(Top|Bottom)(Left|Right)Radius)\s*:\s*['"`]?\d/,
  spacing:
    /\b(padding|margin|gap|rowGap|columnGap)(Top|Bottom|Left|Right|Inline|Block|InlineStart|InlineEnd|BlockStart|BlockEnd)?\s*:\s*['"`]-?\d/,
} as const;

type Category = keyof typeof PATTERNS;

const NOT_READ = ['test/', 'mocks/', 'contract/', 'integration/', 'api/generated/', 'theme/'];

/** Lines matched in each file on bd743911, by category; a file not named had none. */
const BASELINE: Record<Category, Record<string, number>> = {
  fontSize: {
    'components/AccountNumberField.tsx': 2,
    'components/PinInput.tsx': 3,
    'components/dialogs/ChangePinDialog.tsx': 1,
    'components/dialogs/DeleteAccountDialog.tsx': 1,
    'components/dialogs/WithdrawDialog.tsx': 1,
    'components/dialogs/moneyDialogStyles.ts': 20,
    'components/layout/AppErrorBoundary.tsx': 3,
    'components/layout/AppLayout.tsx': 1,
    'components/layout/AuthBrandingPanel.tsx': 5,
    'components/layout/AuthLayout.tsx': 8,
    'components/layout/BottomNav.tsx': 2,
    'components/layout/PageHeader.tsx': 1,
    'components/layout/ProtectedRoute.tsx': 1,
    'components/layout/RouteError.tsx': 2,
    'components/layout/Sidebar.tsx': 5,
    'components/shared/ConfirmDialog.tsx': 4,
    'components/shared/MovementCheckStatus.tsx': 1,
    'components/shared/QuickActionButton.tsx': 1,
    'components/shared/SectionTitle.tsx': 1,
    'components/shared/SegmentedFilter.tsx': 1,
    'components/shared/transactionRowStyles.ts': 8,
    'features/auth/StepUpModal.tsx': 1,
    'features/demo/DemoCopyPanel.tsx': 2,
    'features/demo/DemoEntry.tsx': 1,
    'pages/AboutPage.tsx': 6,
    'pages/AccountsPage.tsx': 8,
    'pages/DashboardPage.tsx': 12,
    'pages/HistoryPage.tsx': 4,
    'pages/LoginPage.tsx': 1,
    'pages/NotFoundPage.tsx': 3,
    'pages/PinSetupPage.tsx': 2,
    'pages/RegisterPage.tsx': 1,
    'pages/SettingsPage.tsx': 12,
    'pages/TransactionDetailPage.tsx': 8,
    'pages/TransferPage.tsx': 5,
    'pages/transferWizardStyles.ts': 18,
  },
  fontWeight: {
    'components/AccountNumberField.tsx': 1,
    'components/PinInput.tsx': 2,
    'components/dialogs/ChangePinDialog.tsx': 1,
    'components/dialogs/WithdrawDialog.tsx': 1,
    'components/dialogs/moneyDialogStyles.ts': 15,
    'components/layout/AppErrorBoundary.tsx': 2,
    'components/layout/AppLayout.tsx': 1,
    'components/layout/AuthBrandingPanel.tsx': 3,
    'components/layout/AuthLayout.tsx': 2,
    'components/layout/BottomNav.tsx': 3,
    'components/layout/PageHeader.tsx': 1,
    'components/layout/ProtectedRoute.tsx': 1,
    'components/layout/RouteError.tsx': 1,
    'components/layout/Sidebar.tsx': 5,
    'components/shared/Avatar.tsx': 1,
    'components/shared/ConfirmDialog.tsx': 3,
    'components/shared/QuickActionButton.tsx': 1,
    'components/shared/SectionTitle.tsx': 1,
    'components/shared/SegmentedFilter.tsx': 1,
    'components/shared/transactionRowStyles.ts': 6,
    'features/demo/DemoCopyPanel.tsx': 1,
    'features/demo/DemoEntry.tsx': 1,
    'pages/AboutPage.tsx': 3,
    'pages/AccountsPage.tsx': 9,
    'pages/DashboardPage.tsx': 10,
    'pages/HistoryPage.tsx': 4,
    'pages/LoginPage.tsx': 1,
    'pages/NotFoundPage.tsx': 2,
    'pages/PinSetupPage.tsx': 1,
    'pages/RegisterPage.tsx': 1,
    'pages/SettingsPage.tsx': 10,
    'pages/TransactionDetailPage.tsx': 5,
    'pages/TransferPage.tsx': 2,
    'pages/transferWizardStyles.ts': 14,
  },
  lineHeight: {
    'components/dialogs/DeleteAccountDialog.tsx': 1,
    'components/dialogs/WithdrawDialog.tsx': 1,
    'components/dialogs/moneyDialogStyles.ts': 2,
    'components/layout/AuthBrandingPanel.tsx': 2,
    'components/layout/AuthLayout.tsx': 2,
    'components/layout/BottomNav.tsx': 2,
    'components/shared/ConfirmDialog.tsx': 1,
    'components/shared/MovementCheckStatus.tsx': 1,
    'features/auth/StepUpModal.tsx': 1,
    'pages/AboutPage.tsx': 2,
    'pages/AccountsPage.tsx': 1,
    'pages/DashboardPage.tsx': 1,
    'pages/PinSetupPage.tsx': 1,
    'pages/TransferPage.tsx': 1,
    'pages/transferWizardStyles.ts': 2,
  },
  borderRadius: {
    'components/PinInput.tsx': 2,
    'components/dialogs/moneyDialogStyles.ts': 10,
    'components/layout/AppErrorBoundary.tsx': 1,
    'components/layout/AppLayout.tsx': 1,
    'components/layout/AuthBrandingPanel.tsx': 1,
    'components/layout/BottomNav.tsx': 6,
    'components/layout/PageHeader.tsx': 1,
    'components/layout/Sidebar.tsx': 4,
    'components/shared/Avatar.tsx': 1,
    'components/shared/ConfirmDialog.tsx': 4,
    'components/shared/QuickActionButton.tsx': 2,
    'components/shared/SegmentedFilter.tsx': 1,
    'components/shared/transactionRowStyles.ts': 2,
    'features/auth/StepUpModal.tsx': 1,
    'pages/AboutPage.tsx': 2,
    'pages/AccountsPage.tsx': 8,
    'pages/DashboardPage.tsx': 6,
    'pages/HistoryPage.tsx': 2,
    'pages/PinSetupPage.tsx': 5,
    'pages/SettingsPage.tsx': 4,
    'pages/TransactionDetailPage.tsx': 6,
    'pages/TransferPage.tsx': 3,
    'pages/transferWizardStyles.ts': 6,
  },
  spacing: {
    'components/AccountNumberField.tsx': 3,
    'components/PinInput.tsx': 10,
    'components/dialogs/ChangePinDialog.tsx': 2,
    'components/dialogs/DeleteAccountDialog.tsx': 2,
    'components/dialogs/DepositDialog.tsx': 1,
    'components/dialogs/WithdrawDialog.tsx': 3,
    'components/dialogs/moneyDialogStyles.ts': 23,
    'components/feedback/AlertSlot.tsx': 1,
    'components/feedback/WaitHint.tsx': 1,
    'components/layout/AppErrorBoundary.tsx': 4,
    'components/layout/AppLayout.tsx': 3,
    'components/layout/AuthBrandingPanel.tsx': 8,
    'components/layout/AuthLayout.tsx': 9,
    'components/layout/BottomNav.tsx': 7,
    'components/layout/PageHeader.tsx': 1,
    'components/layout/ProtectedRoute.tsx': 4,
    'components/layout/RouteAnnouncer.tsx': 1,
    'components/layout/RouteError.tsx': 2,
    'components/layout/Sidebar.tsx': 17,
    'components/shared/ConfirmDialog.tsx': 6,
    'components/shared/MovementCheckStatus.tsx': 1,
    'components/shared/QuickActionButton.tsx': 1,
    'components/shared/SegmentedFilter.tsx': 3,
    'components/shared/transactionRowStyles.ts': 11,
    'features/auth/SessionExpiryWarning.tsx': 3,
    'features/auth/StepUpModal.tsx': 2,
    'features/demo/DemoCopyPanel.tsx': 1,
    'features/demo/DemoEntry.tsx': 2,
    'features/demo/StartOverDialog.tsx': 1,
    'pages/AboutPage.tsx': 11,
    'pages/AccountsPage.tsx': 27,
    'pages/DashboardPage.tsx': 27,
    'pages/HistoryPage.tsx': 10,
    'pages/InternalTransferPage.tsx': 3,
    'pages/LoginPage.tsx': 6,
    'pages/NotFoundPage.tsx': 2,
    'pages/PinSetupPage.tsx': 5,
    'pages/RegisterPage.tsx': 7,
    'pages/SettingsPage.tsx': 16,
    'pages/TransactionDetailPage.tsx': 14,
    'pages/TransferPage.tsx': 9,
    'pages/transferWizardStyles.ts': 19,
  },
};

/** The paths under `src` the count reads, with forward slashes on every platform. */
function sourceFiles(): string[] {
  return readdirSync('src', { recursive: true, encoding: 'utf8' })
    .map((path) => path.replaceAll('\\', '/'))
    .filter((path) => /\.tsx?$/.test(path) && !/\.test\.tsx?$/.test(path))
    .filter((path) => !NOT_READ.some((folder) => path.startsWith(folder)));
}

function isComment(line: string): boolean {
  const trimmed = line.trim();
  return trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*');
}

function countNow(): Record<Category, Record<string, number>> {
  const counts = Object.fromEntries(
    Object.keys(PATTERNS).map((category) => [category, {} as Record<string, number>]),
  ) as Record<Category, Record<string, number>>;
  for (const path of sourceFiles()) {
    const lines = readFileSync(`src/${path}`, 'utf8')
      .split('\n')
      .filter((line) => !isComment(line));
    for (const category of Object.keys(PATTERNS) as Category[]) {
      const matched = lines.filter((line) => PATTERNS[category].test(line)).length;
      if (matched > 0) counts[category][path] = matched;
    }
  }
  return counts;
}

const sum = (files: Record<string, number>) => Object.values(files).reduce((a, b) => a + b, 0);

const NOW = countNow();

describe('style literals outside src/theme', () => {
  const rows = (Object.keys(PATTERNS) as Category[]).map((category) => ({
    category,
    now: sum(NOW[category]),
    allowed: sum(BASELINE[category]),
  }));

  it.each(rows)(
    '$category: $now lines with a literal, at most $allowed',
    ({ category, now, allowed }) => {
      const grew = Object.entries(NOW[category])
        .filter(([path, lines]) => lines > (BASELINE[category][path] ?? 0))
        .map(([path, lines]) => `${path} ${lines} (was ${BASELINE[category][path] ?? 0})`);
      expect(
        now,
        `${category}: ${now} lines with a literal, above the ${allowed} allowed. Grew: ` +
          `${grew.join(', ')}. Use a role from src/theme or a Fluent token instead. A change ` +
          `that removes literals lowers BASELINE in src/theme/styleLiterals.test.ts: each file ` +
          `it touched to the lines it now matches, a file at zero taken out.`,
      ).toBeLessThanOrEqual(allowed);
    },
  );

  it('reads the files the baseline was counted from', () => {
    // CONTROL: a count that read nothing would pass every ratchet above.
    expect(sum(NOW.spacing)).toBeGreaterThan(100);
    expect(Object.keys(NOW.fontSize)).toContain('pages/DashboardPage.tsx');
  });
});
