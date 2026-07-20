import { screen } from '@testing-library/react';
import { Button } from '@fluentui/react-components';
import { Link } from 'react-router-dom';
import { renderWithProviders } from './renderWithProviders';

describe('renderWithProviders', () => {
  it('renders under theme + store + router', () => {
    renderWithProviders(
      <div>
        <Button>Themed button</Button>
        <Link to="/accounts">Accounts</Link>
      </div>,
    );

    expect(screen.getByRole('button', { name: 'Themed button' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Accounts' })).toHaveAttribute('href', '/accounts');
  });

  it('gives every test a FRESH store', () => {
    const { store } = renderWithProviders(<div />);
    const { store: another } = renderWithProviders(<div />);

    expect(store).not.toBe(another);
    expect(store.getState()).toHaveProperty('auth');
    expect(store.getState()).toHaveProperty('api');
  });
});
