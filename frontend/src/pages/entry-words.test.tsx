import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { LoginPage } from './LoginPage';
import { RegisterPage } from './RegisterPage';

/**
 * The words on the two entry pages.
 *
 * The brand panel is `display: none` in jsdom, which does not evaluate the media query that shows
 * it, so a role or text query would miss the headline and the intro. They are read from the panel,
 * as in auth-layout.test.tsx.
 */

const PASSWORD_HINT =
  'At least 8 characters, with an uppercase letter, a lowercase letter, a number and one character that is not a letter or a digit, such as ! or #.';
const EMAIL_EMPTY = 'Enter your email address.';
const EMAIL_MALFORMED = 'Enter an email address in the correct format, like name@example.com.';
const EMAIL_OLD = 'Please enter a valid email address';
const PASSWORD_ASCII = 'Use only letters A to Z, numbers and the symbols on a standard keyboard.';
const PASSWORD_SYMBOL = 'Password must contain at least one special character';

function text(node: Element | null | undefined): string | null {
  const value = node?.textContent?.replace(/\s+/g, ' ').trim();
  return value ? value : null;
}

/** The footer sentences are text nodes beside the contact link, so no element holds one of them alone. */
function columnHas(container: HTMLElement, sentence: string): boolean {
  const column = container.querySelector('main')?.textContent?.replace(/\s+/g, ' ') ?? '';
  return column.includes(sentence);
}

function renderRegister() {
  return renderWithProviders(<RegisterPage />, { routerEntries: ['/register'] });
}

function renderLogin() {
  return renderWithProviders(<LoginPage />, { routerEntries: ['/login'] });
}

async function submit(name: string | RegExp) {
  await userEvent.click(screen.getByRole('button', { name }));
}

describe('registration', () => {
  it('says only what is true, in sentence case', () => {
    const { container } = renderRegister();
    const [headline, intro] = container.querySelectorAll('aside p');
    const page = container.textContent ?? '';

    // One comparison, so a run against the old copy names every string that is still wrong.
    expect({
      headline: text(headline),
      intro: text(intro),
      title: text(screen.getByRole('heading', { level: 1 })),
      button: text(screen.getByRole('button', { name: /create account/i })),
      firstName: screen.queryByLabelText('First name') !== null,
      lastName: screen.queryByLabelText('Last name') !== null,
      email: screen.queryByLabelText('Email address') !== null,
      password: screen.queryByLabelText('Password') !== null,
      confirmPassword: screen.queryByLabelText('Confirm password') !== null,
      hint: screen.queryByText(PASSWORD_HINT) !== null,
      signIn: screen.queryByRole('link', { name: 'Sign in' })?.getAttribute('href') ?? null,
      demo: columnHas(container, 'Demo project — not a real bank.'),
      contact:
        screen.queryByRole('link', { name: 'Contact the developer →' })?.getAttribute('href') ??
        null,
      joinThousands: page.includes('Join thousands'),
      terms: page.includes('Terms of Service'),
      journey: page.includes('Start Your Financial Journey'),
    }).toEqual({
      headline: 'Start banking in a minute',
      intro: 'Open an account, then deposit, withdraw and send money in a working demo.',
      title: 'Create account',
      button: 'Create account',
      firstName: true,
      lastName: true,
      email: true,
      password: true,
      confirmPassword: true,
      hint: true,
      signIn: '/login',
      demo: true,
      contact: '/contact',
      joinThousands: false,
      terms: false,
      journey: false,
    });
  });

  it('refuses a password the server refuses for an accented letter', async () => {
    renderRegister();
    // Both fields, so a mismatch is not the error under test. Pässword1! meets every rule the
    // form already had; the accented letter is what the server refuses (printable ASCII only).
    fireEvent.change(screen.getByLabelText(/^password$/i), { target: { value: 'Pässword1!' } });
    fireEvent.change(screen.getByLabelText(/^confirm password$/i), {
      target: { value: 'Pässword1!' },
    });
    await submit(/create account/i);

    const field = screen.getByLabelText(/^password$/i).closest('.fui-Field');
    await waitFor(() => expect(field).toHaveTextContent(PASSWORD_ASCII));
  });

  it('still asks for a symbol when the password is otherwise printable ASCII', async () => {
    renderRegister();
    fireEvent.change(screen.getByLabelText(/^password$/i), { target: { value: 'Password1' } });
    fireEvent.change(screen.getByLabelText(/^confirm password$/i), {
      target: { value: 'Password1' },
    });
    await submit(/create account/i);

    const field = screen.getByLabelText(/^password$/i).closest('.fui-Field');
    await waitFor(() => expect(field).toHaveTextContent(PASSWORD_SYMBOL));
  });

  it('asks for an email address when the field is empty', async () => {
    renderRegister();
    await submit(/create account/i);
    await emailSentences({ empty: true, malformed: false, old: false });
  });

  it('asks for the right format when the field is not an email address', async () => {
    renderRegister();
    fireEvent.change(screen.getByRole('textbox', { name: /email/i }), {
      target: { value: 'name' },
    });
    await submit(/create account/i);
    await emailSentences({ empty: false, malformed: true, old: false });
  });
});

describe('sign-in', () => {
  it('names the page in sentence case and keeps both sentences under the form', () => {
    const { container } = renderLogin();
    const headline = container.querySelector('aside p');

    expect({
      headline: text(headline),
      encryption: columnHas(
        container,
        'Protected by bank-grade encryption. We never share your details.',
      ),
      demo: columnHas(container, 'Demo project — not a real bank.'),
    }).toEqual({
      headline: 'Banking made simple, secure and smart',
      encryption: true,
      demo: true,
    });
  });

  it('asks for an email address when the field is empty', async () => {
    renderLogin();
    await submit('Sign in');
    await emailSentences({ empty: true, malformed: false, old: false });
  });

  it('asks for the right format when the field is not an email address', async () => {
    renderLogin();
    fireEvent.change(screen.getByRole('textbox', { name: /email/i }), {
      target: { value: 'name' },
    });
    await submit('Sign in');
    await emailSentences({ empty: false, malformed: true, old: false });
  });
});

async function emailSentences(expected: { empty: boolean; malformed: boolean; old: boolean }) {
  await waitFor(() => {
    expect(
      screen.queryByText(EMAIL_EMPTY) ??
        screen.queryByText(EMAIL_MALFORMED) ??
        screen.queryByText(EMAIL_OLD),
    ).not.toBeNull();
  });
  expect({
    empty: screen.queryByText(EMAIL_EMPTY) !== null,
    malformed: screen.queryByText(EMAIL_MALFORMED) !== null,
    old: screen.queryByText(EMAIL_OLD) !== null,
  }).toEqual(expected);
}
