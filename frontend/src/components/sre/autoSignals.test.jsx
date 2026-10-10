import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';

const api = vi.hoisted(() => ({
  getAutoSignals: vi.fn(),
  updateAutoSignals: vi.fn()
}));

vi.mock('../../api', () => ({ sdkApi: api, remediationTargetsApi: {} }));
vi.mock('../Toast', () => ({ useToast: () => ({ addToast: vi.fn() }) }));

import { AutoSignalSettings } from './ConnectApp';

describe('Automatic signals in Connect an App', () => {
  beforeEach(() => {
    api.getAutoSignals.mockReset();
    api.updateAutoSignals.mockReset();
  });

  it('shows what is measured automatically, with both optional signals on by default', async () => {
    api.getAutoSignals.mockResolvedValue({ autoQueueDepth: true, autoRetries: true, retryWindowSeconds: 10 });

    render(<AutoSignalSettings projectId="p1" credentialId="c1" />);

    expect(await screen.findByText(/no code in your app/)).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: /Queue depth/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Retries/ })).toBeChecked();
    expect(screen.getByLabelText('Retry window in seconds')).toHaveValue(10);
    expect(api.getAutoSignals).toHaveBeenCalledWith('p1', 'c1');
  });

  it('saves a switched-off signal for this app only', async () => {
    api.getAutoSignals.mockResolvedValue({ autoQueueDepth: true, autoRetries: true, retryWindowSeconds: 10 });
    api.updateAutoSignals.mockImplementation(async (_p, _c, next) => next);
    render(<AutoSignalSettings projectId="p1" credentialId="c1" />);

    fireEvent.click(await screen.findByRole('checkbox', { name: /Retries/ }));

    await waitFor(() => expect(api.updateAutoSignals).toHaveBeenCalledWith('p1', 'c1',
      { autoQueueDepth: true, autoRetries: false, retryWindowSeconds: 10 }));
    expect(screen.queryByLabelText('Retry window in seconds')).not.toBeInTheDocument();
  });

  it('refuses a retry window outside 1-300 seconds without saving it', async () => {
    api.getAutoSignals.mockResolvedValue({ autoQueueDepth: true, autoRetries: true, retryWindowSeconds: 10 });
    render(<AutoSignalSettings projectId="p1" credentialId="c1" />);
    const input = await screen.findByLabelText('Retry window in seconds');

    fireEvent.change(input, { target: { value: '900' } });
    fireEvent.blur(input);

    expect(await screen.findByRole('alert')).toHaveTextContent('between 1 and 300');
    expect(api.updateAutoSignals).not.toHaveBeenCalled();
  });

  it('puts the previous value back when saving fails', async () => {
    api.getAutoSignals.mockResolvedValue({ autoQueueDepth: true, autoRetries: true, retryWindowSeconds: 10 });
    api.updateAutoSignals.mockRejectedValue(new Error('Backend unavailable'));
    render(<AutoSignalSettings projectId="p1" credentialId="c1" />);
    const queue = await screen.findByRole('checkbox', { name: /Queue depth/ });

    fireEvent.click(queue);

    expect(await screen.findByRole('alert')).toHaveTextContent('Backend unavailable');
    expect(screen.getByRole('checkbox', { name: /Queue depth/ })).toBeChecked();
  });
});
