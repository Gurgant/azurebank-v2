import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { RegisterPage } from './RegisterPage';

// jsdom lays nothing out, so the row's fit is read from the styles that allow it: each name Field
// may shrink below its content (a flex item's `min-width: auto` would hold it at the input's
// built-in 217 px), and each Input root fills its Field instead of keeping that width. Measured in
// Chromium, these are what keep the row inside a 320 px screen. The row aligns its fields to the
// top, so a message under one name field does not stretch the other field's input.
function nameBoxes(label: string) {
  const input = screen.getByLabelText(label);
  const field = input.closest('.fui-Field');
  const root = input.closest('.fui-Input');
  if (!(field instanceof HTMLElement) || !(root instanceof HTMLElement)) {
    throw new Error(`No Fluent Field and Input around "${label}"`);
  }
  return {
    rowAlignItems: field.parentElement ? getComputedStyle(field.parentElement).alignItems : '',
    fieldMinWidth: getComputedStyle(field).minWidth,
    rootWidth: getComputedStyle(root).width,
    rootMinWidth: getComputedStyle(root).minWidth,
  };
}

describe('the registration name row', () => {
  it('lets both name fields shrink to their column, aligned to the top', () => {
    renderWithProviders(<RegisterPage />, { routerEntries: ['/register'] });

    expect({
      first: nameBoxes('First Name'),
      last: nameBoxes('Last Name'),
    }).toStrictEqual({
      first: {
        rowAlignItems: 'flex-start',
        fieldMinWidth: '0px',
        rootWidth: '100%',
        rootMinWidth: '0px',
      },
      last: {
        rowAlignItems: 'flex-start',
        fieldMinWidth: '0px',
        rootWidth: '100%',
        rootMinWidth: '0px',
      },
    });
  });
});
