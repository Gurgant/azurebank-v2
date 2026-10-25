import { SignOut24Regular } from '@fluentui/react-icons';

/**
 * The act that ends this session, named once. Sign in and Sign out, never Log in or Log out.
 *
 * Three controls say it: the sidebar's, the phone's row under More and the Settings page's. When
 * each typed its own words, Settings came to say "Log out" while the other two said "Sign out".
 * It lives beside `SignOutIcon.tsx` rather than in that file, because a component module
 * that also exports a constant loses React Refresh.
 */
export const SIGN_OUT_ACTION = {
  label: 'Sign out',
  icon: SignOut24Regular,
} as const;
