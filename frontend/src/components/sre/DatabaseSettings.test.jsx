import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import DatabaseSettings from './DatabaseSettings';
import { databaseConfigApi } from '../../api';
vi.mock('../../api', () => ({ databaseConfigApi: { getConfig: vi.fn(), testConnection: vi.fn(), saveConfig: vi.fn() } }));
const initial = { activeProvider: 'SQLite', selected: { provider: 'SQLite' }, requiresRestart: false };
beforeEach(() => { vi.clearAllMocks(); databaseConfigApi.getConfig.mockResolvedValue(initial); });
describe('Database settings', () => {
  it('keeps SQLite default and tests without SQL credentials', async () => {
    databaseConfigApi.testConnection.mockResolvedValue({ success: true, message: 'Local database available' });
    render(<DatabaseSettings />);
    await waitFor(() => expect(screen.getByLabelText('Storage engine')).toBeEnabled());
    expect(screen.queryByLabelText('Database password')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Test database connection' }));
    expect(databaseConfigApi.testConnection).toHaveBeenCalledWith({ provider: 'SQLite' });
  });
  it('masks and clears a SQL password after saving and explains restart', async () => {
    databaseConfigApi.saveConfig.mockResolvedValue({ activeProvider: 'SQLite', selected: { provider: 'SqlServer', authentication: 'SqlLogin', hasPassword: true }, requiresRestart: true });
    render(<DatabaseSettings />);
    await waitFor(() => expect(screen.getByLabelText('Storage engine')).toBeEnabled());
    await userEvent.selectOptions(screen.getByLabelText('Storage engine'), 'SqlServer');
    await userEvent.selectOptions(screen.getByLabelText('Database authentication'), 'SqlLogin');
    const password = screen.getByLabelText('Database password'); expect(password).toHaveAttribute('type', 'password');
    await userEvent.type(password, 'test-private-password');
    await userEvent.click(screen.getByRole('button', { name: 'Save database settings' }));
    expect(await screen.findByRole('status')).toHaveTextContent('Restart the KAIRON backend');
    expect(password).toHaveValue(''); expect(screen.queryByText('test-private-password')).not.toBeInTheDocument();
  });
  it('does not display raw transport errors containing credentials', async () => {
    databaseConfigApi.testConnection.mockRejectedValue(new Error('Password=private-value'));
    render(<DatabaseSettings />); await waitFor(() => expect(screen.getByLabelText('Storage engine')).toBeEnabled());
    await userEvent.click(screen.getByRole('button', { name: 'Test database connection' }));
    expect(await screen.findByRole('status')).toHaveTextContent('operation could not be completed');
    expect(screen.queryByText(/private-value/)).not.toBeInTheDocument();
  });
});
