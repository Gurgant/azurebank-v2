import { tokens } from '@fluentui/react-components';

// The ring the app draws on a container that was given focus (`index.css`), for the paragraphs
// and titles that take focus when their view appears. Without it the browser draws its own
// default.
export const landingRing = {
  ':focus-visible': {
    outline: `2px solid ${tokens.colorStrokeFocus2}`,
    outlineOffset: '2px',
  },
};
