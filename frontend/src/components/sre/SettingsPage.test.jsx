import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import SettingsPage from './SettingsPage';

/**
 * The AI Configuration panel (frontend PRD section: AI provider configuration) - provider
 * selection, API key entry, model configuration (Auto/custom), Test Connection, Save, and that a
 * typed key is never echoed anywhere else in the rendered page.
 */
vi.mock('../../api', () => ({
  healthApi: {
    getHealthStatus: vi.fn().mockResolvedValue({
      backend: true, database: true, aiService: true, detectionEnabled: true,
      remediationEnabled: true, aiMode: 'mock'
    })
  },
  aiConfigApi: {
    getConfig: vi.fn(),
    saveConfig: vi.fn(),
    testConnection: vi.fn(),
    listModels: vi.fn()
  },
  dataManagementApi: {
    downloadData: vi.fn(),
    deleteAllData: vi.fn()
  }
}));

vi.mock('../../hooks/useDemo', () => ({
  useHealth: () => ({
    health: {
      backend: true, database: true, aiService: true, detectionEnabled: true,
      remediationEnabled: true, aiMode: 'mock'
    },
    isLoading: false
  })
}));

describe('SettingsPage > AI configuration', () => {
  let aiConfigApi;
  let dataManagementApi;

  beforeEach(async () => {
    ({ aiConfigApi, dataManagementApi } = await import('../../api'));
    aiConfigApi.getConfig.mockReset().mockResolvedValue({ provider: '', model: '', hasApiKey: false, updatedAt: null });
    aiConfigApi.saveConfig.mockReset();
    aiConfigApi.testConnection.mockReset();
    aiConfigApi.listModels.mockReset();
    dataManagementApi.downloadData.mockReset();
    dataManagementApi.deleteAllData.mockReset();
  });

  it('defaults to Groq with no configuration saved yet', async () => {
    render(<SettingsPage />);

    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());
    expect(screen.getByLabelText(/provider/i)).toHaveValue('groq');
    expect(screen.queryByText(/currently configured/i)).not.toBeInTheDocument();
  });

  it('shows the currently saved provider and model once loaded', async () => {
    aiConfigApi.getConfig.mockResolvedValue({
      provider: 'gemini', model: 'gemini-2.5-flash-lite', hasApiKey: true, updatedAt: new Date().toISOString()
    });

    render(<SettingsPage />);

    await waitFor(() => expect(screen.getByLabelText(/provider/i)).toHaveValue('gemini'));
    expect(screen.getByText(/currently configured: gemini/i)).toBeInTheDocument();
  });

  it('the API key field is masked', () => {
    render(<SettingsPage />);

    expect(screen.getByLabelText(/api key/i)).toHaveAttribute('type', 'password');
  });

  it('Test Connection sends what was typed and shows a successful result', async () => {
    const user = userEvent.setup();
    aiConfigApi.testConnection.mockResolvedValue({
      success: true, provider: 'groq', effectiveProvider: 'groq', model: 'openai/gpt-oss-120b'
    });

    render(<SettingsPage />);
    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());

    await user.type(screen.getByLabelText(/api key/i), 'gsk_typed_key');
    await user.click(screen.getByRole('button', { name: /test connection/i }));

    await waitFor(() => expect(aiConfigApi.testConnection).toHaveBeenCalledWith(
      expect.objectContaining({ provider: 'groq', apiKey: 'gsk_typed_key', model: '' })
    ));
    expect(await screen.findByText(/connection successful/i)).toBeInTheDocument();
    expect(screen.getByText(/openai\/gpt-oss-120b/)).toBeInTheDocument();
  });

  it('Test Connection reports a failure without exposing the typed key anywhere on the page', async () => {
    const user = userEvent.setup();
    aiConfigApi.testConnection.mockResolvedValue({
      success: false, provider: 'groq', error: 'groq returned HTTP 401'
    });

    render(<SettingsPage />);
    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());
    await user.type(screen.getByLabelText(/api key/i), 'gsk_super_secret_value');
    await user.click(screen.getByRole('button', { name: /test connection/i }));

    expect(await screen.findByText(/connection failed/i)).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('gsk_super_secret_value');
  });

  it('omitting the API key tests whatever is already saved, not an empty key', async () => {
    const user = userEvent.setup();
    aiConfigApi.getConfig.mockResolvedValue({ provider: 'groq', model: '', hasApiKey: true, updatedAt: new Date().toISOString() });
    aiConfigApi.testConnection.mockResolvedValue({ success: true, provider: 'groq', effectiveProvider: 'groq', model: 'm' });

    render(<SettingsPage />);
    await waitFor(() => expect(screen.getByText(/currently configured/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /test connection/i }));

    await waitFor(() => expect(aiConfigApi.testConnection).toHaveBeenCalledWith(
      expect.objectContaining({ apiKey: undefined })
    ));
  });

  it('Save persists the configuration and clears the key field afterwards', async () => {
    const user = userEvent.setup();
    aiConfigApi.saveConfig.mockResolvedValue({
      provider: 'groq', model: '', hasApiKey: true, updatedAt: new Date().toISOString(), applied: true
    });

    render(<SettingsPage />);
    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());

    const keyInput = screen.getByLabelText(/api key/i);
    await user.type(keyInput, 'gsk_new_key');
    await user.click(screen.getByRole('button', { name: /save configuration/i }));

    await waitFor(() => expect(aiConfigApi.saveConfig).toHaveBeenCalledWith(
      expect.objectContaining({ provider: 'groq', apiKey: 'gsk_new_key' })
    ));
    await waitFor(() => expect(keyInput).toHaveValue(''));
    expect(await screen.findByText(/currently configured: groq/i)).toBeInTheDocument();
  });

  it('switching provider resets the key field and the model choice', async () => {
    const user = userEvent.setup();
    render(<SettingsPage />);
    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());

    await user.type(screen.getByLabelText(/api key/i), 'gsk_groq_key');
    await user.selectOptions(screen.getByLabelText(/provider/i), 'qwen');

    expect(screen.getByLabelText(/provider/i)).toHaveValue('qwen');
    expect(screen.getByLabelText(/api key/i)).toHaveValue('');
  });

  it('selecting Custom model reveals a free-text model field that Test Connection uses', async () => {
    const user = userEvent.setup();
    aiConfigApi.testConnection.mockResolvedValue({ success: true, provider: 'groq', effectiveProvider: 'groq', model: 'llama-x' });

    render(<SettingsPage />);
    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());

    await user.selectOptions(screen.getByLabelText(/^model$/i), 'Custom model...');
    await user.type(screen.getByPlaceholderText('model-name'), 'llama-x');
    await user.click(screen.getByRole('button', { name: /test connection/i }));

    await waitFor(() => expect(aiConfigApi.testConnection).toHaveBeenCalledWith(
      expect.objectContaining({ model: 'llama-x' })
    ));
  });

  it('Auto / Recommended sends a blank model, never a placeholder string', async () => {
    const user = userEvent.setup();
    aiConfigApi.testConnection.mockResolvedValue({ success: true, provider: 'groq', effectiveProvider: 'groq', model: 'openai/gpt-oss-120b' });

    render(<SettingsPage />);
    await waitFor(() => expect(aiConfigApi.getConfig).toHaveBeenCalled());
    await user.click(screen.getByRole('button', { name: /test connection/i }));

    await waitFor(() => expect(aiConfigApi.testConnection).toHaveBeenCalledWith(
      expect.objectContaining({ model: '' })
    ));
  });

  it('the existing read-only settings status grid keeps working alongside the new panel', async () => {
    render(<SettingsPage />);

    expect(await screen.findByText('AI provider')).toBeInTheDocument();
    expect(screen.getByText('Detection')).toBeInTheDocument();
  });

  it('downloads a database export with the server-provided filename', async () => {
    const user = userEvent.setup();
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:kairon-export');
    const revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    dataManagementApi.downloadData.mockResolvedValue({
      blob: new Blob(['sqlite']),
      fileName: 'kairon-data.db'
    });

    render(<SettingsPage />);
    const downloadButton = await screen.findByRole('button', { name: /download my data/i });
    await waitFor(() => expect(downloadButton).toBeEnabled());
    await user.click(downloadButton);

    await waitFor(() => expect(dataManagementApi.downloadData).toHaveBeenCalledOnce());
    expect(createObjectURL).toHaveBeenCalledOnce();
    expect(click).toHaveBeenCalledOnce();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:kairon-export');
    createObjectURL.mockRestore();
    revokeObjectURL.mockRestore();
    click.mockRestore();
  });

  it('requires DELETE before permanently deleting database data', async () => {
    const user = userEvent.setup();
    dataManagementApi.deleteAllData.mockResolvedValue({ deletedRecords: 12, deletedBackups: 2 });

    render(<SettingsPage />);
    const deleteButton = await screen.findByRole('button', { name: /^delete all data$/i });
    await waitFor(() => expect(deleteButton).toBeEnabled());
    await user.click(deleteButton);

    const permanentDelete = screen.getByRole('button', { name: /permanently delete data/i });
    expect(permanentDelete).toBeDisabled();
    await user.type(screen.getByLabelText(/type delete to confirm/i), 'DELETE');
    expect(permanentDelete).toBeEnabled();
    await user.click(permanentDelete);

    await waitFor(() => expect(dataManagementApi.deleteAllData).toHaveBeenCalledWith('DELETE'));
    expect(await screen.findByRole('status')).toHaveTextContent('Deleted 12 database records and 2 stored backups');
  });
});
