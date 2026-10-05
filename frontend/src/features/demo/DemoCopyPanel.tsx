import { useId, useState, useSyncExternalStore } from 'react';
import { Button, Text, makeStyles, mergeClasses } from '@fluentui/react-components';
import { useAppSelector } from '../../app/hooks';
import { colors } from '../../theme/tokens';
import { formatDateTime } from '../../utils/format';
import { selectCurrentUser } from '../auth/authSlice';
import { getDemoCopySnapshot, isDemoCopyOwner, subscribeDemoCopy } from './demoCopyStorage';
import { isDemoMode } from './demoMode';
import { DEMO_PIN } from './demoPin';
import {
  COPY_IS_A_DEMO,
  HIDE_SIGN_IN_DETAILS,
  SHOW_SIGN_IN_DETAILS,
  START_OVER_CONFIRM,
  YOUR_PRIVATE_COPY,
  contactsYouCanPay,
  copyIsYours,
  copyPin,
  signInDetails,
} from './demoWords';
import { StartOverDialog } from './StartOverDialog';

const useStyles = makeStyles({
  // A column of lines and buttons, each as wide as its words: a button left to stretch would be
  // as wide as the card, and so would its press.
  panel: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '8px',
  },

  // The dashboard's section titles, in size and weight (src/pages/DashboardPage.tsx).
  // `display: block` because Fluent's `Text` renders inline whatever element it is asked for, and
  // no margin because a heading tag brings the browser's own with it
  // (src/components/layout/PageHeader.tsx).
  heading: {
    display: 'block',
    margin: 0,
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  // A paragraph tag brings the browser's margin too; the column's gap is the spacing.
  line: {
    display: 'block',
    margin: 0,
    fontSize: '14px',
    color: colors.neutral[600],
  },

  // An address and a password have no space to break at: on a narrow screen they wrap inside the
  // card where they must, and do not push the page sideways.
  details: {
    overflowWrap: 'anywhere',
  },
});

/**
 * The dashboard's word about the demo copy the visitor is signed in to: what it is, how long it
 * works, its PIN, and, for its owner, whom it can pay, what signs in to it and a way to start
 * over. On a page that is not the demo, and while nobody is signed in, it draws nothing at all.
 *
 * THE OWNER is whoever is signed in on the browser that keeps the copy's sign-in details: the
 * kept copy's address is the signed-in user's (src/features/demo/demoCopyStorage.ts). Anyone else
 * signed in to the copy typed its email and password on another browser. They read what the copy
 * is and its PIN, which is the same on every copy. They are shown nothing this browser keeps, and
 * no "Start over": the dialog behind it speaks of the copy this browser keeps
 * (src/features/demo/demoWords.ts), and this browser does not keep theirs.
 *
 * The kept copy is read each time the panel is drawn, and never held here. What another tab
 * forgot or replaced is gone from this panel at its next render, the open sign-in details
 * included. A tab whose browser refused to store its copy is the exception: it is drawn from the
 * copy that tab holds in memory (src/features/demo/demoCopyStorage.ts).
 *
 * EVERY KEPT VALUE IS DRAWN AS TEXT. The key is the browser's storage, which anything on the
 * page's origin can write to, so what it holds is input: a handle or a password that is markup is
 * shown as the characters it is.
 *
 * The sign-in details are in the page only while they are shown, under the button that shows
 * them. That button says what a press will do in its name, and whether the details are on the
 * page in `aria-expanded`. The house's other reveal buttons carry their state in the name alone
 * (src/components/PinInput.tsx): "Hide PIN, pressed" read as a PIN that is hidden. "Hide sign-in
 * details, expanded" does not contradict itself: one says what the button does, the other what
 * is open.
 *
 * "Start over" opens the one dialog that asks before a new copy takes this one's place
 * (src/features/demo/StartOverDialog.tsx), kept beside the panel for the owner, closed, and not
 * inside it: the dialog has a heading, an alert and buttons of its own, and the panel's region
 * holds the panel's alone. A new copy needs nothing more from here. The claim's answer signs the
 * new owner in (src/features/auth/authSlice.ts) and the new copy is kept in the same dispatch
 * (src/features/auth/sessionMiddleware.ts), so the next time the panel is drawn it is the
 * owner's, for the new copy.
 *
 * `className` is the card of the page that shows the panel. The page hands it over, and does not
 * wrap the panel in a card of its own, because a panel that draws nothing must leave no empty
 * card behind.
 */
export function DemoCopyPanel({ className }: { className?: string }) {
  const styles = useStyles();
  const user = useAppSelector(selectCurrentUser);
  const kept = useSyncExternalStore(subscribeDemoCopy, getDemoCopySnapshot);
  const headingId = useId();
  const detailsId = useId();
  const [detailsShown, setDetailsShown] = useState(false);
  const [startingOver, setStartingOver] = useState(false);

  // Asked here, in the component, and not when this module loads: the page says whether it is
  // the demo with a tag (src/features/demo/demoMode.ts).
  if (!isDemoMode() || !user) return null;

  // The owner's copy, or `null` for a visitor whose copy this browser does not keep.
  const copy = isDemoCopyOwner(kept, user) ? kept : null;

  return (
    <>
      <section className={mergeClasses(styles.panel, className)} aria-labelledby={headingId}>
        <Text as="h2" id={headingId} className={styles.heading}>
          {YOUR_PRIVATE_COPY}
        </Text>
        <Text as="p" className={styles.line}>
          {copy ? copyIsYours(formatDateTime(copy.expiresAt)) : COPY_IS_A_DEMO}
        </Text>
        <Text as="p" className={styles.line}>
          {copyPin(DEMO_PIN)}
        </Text>
        {copy && copy.contacts.length > 0 && (
          <Text as="p" className={styles.line}>
            {contactsYouCanPay(copy.contacts)}
          </Text>
        )}
        {copy && (
          <>
            <Button
              aria-expanded={detailsShown}
              aria-controls={detailsId}
              onClick={() => setDetailsShown((shown) => !shown)}
            >
              {detailsShown ? HIDE_SIGN_IN_DETAILS : SHOW_SIGN_IN_DETAILS}
            </Button>
            {detailsShown && (
              <Text as="p" id={detailsId} className={mergeClasses(styles.line, styles.details)}>
                {signInDetails(copy.email, copy.password)}
              </Text>
            )}
            <Button onClick={() => setStartingOver(true)}>{START_OVER_CONFIRM}</Button>
          </>
        )}
      </section>
      {copy && <StartOverDialog isOpen={startingOver} onClose={() => setStartingOver(false)} />}
    </>
  );
}
