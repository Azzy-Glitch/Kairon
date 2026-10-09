import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import CloseIncident, { canCloseIncident } from './CloseIncident';
import { IncidentStatus } from '../../types/incident';

const open = { id: 'inc-1', status: IncidentStatus.RecommendationReady };

describe('CloseIncident', () => {
  beforeEach(() => { try { localStorage.clear(); } catch { /* ignore */ } });

  it('is offered only while the incident is open and nothing is running', () => {
    expect(canCloseIncident(open)).toBe(true);
    expect(canCloseIncident({ status: IncidentStatus.Detected })).toBe(true);
    expect(canCloseIncident({ status: IncidentStatus.AwaitingApproval })).toBe(true);
    for (const status of [IncidentStatus.Remediating, IncidentStatus.Verifying, IncidentStatus.Resolved,
      IncidentStatus.Failed, IncidentStatus.Rejected, IncidentStatus.Cancelled]) {
      expect(canCloseIncident({ status })).toBe(false);
    }
  });

  it('renders nothing for a closed incident', () => {
    const { container } = render(<CloseIncident incident={{ id: 'x', status: IncidentStatus.Resolved }} onClose={vi.fn()} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('asks for confirmation and a name before closing', async () => {
    const onClose = vi.fn().mockResolvedValue({});
    render(<CloseIncident incident={open} onClose={onClose} />);

    fireEvent.click(screen.getByRole('button', { name: 'Close incident' }));
    expect(onClose).not.toHaveBeenCalled();

    const confirm = screen.getByRole('button', { name: 'Close incident' });
    expect(confirm).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: '  Azzy ' } });
    fireEvent.change(screen.getByLabelText('Reason (optional)'), { target: { value: 'Diagnosed before the target was enabled' } });
    fireEvent.click(confirm);

    await waitFor(() => expect(onClose).toHaveBeenCalledWith('Azzy', 'Diagnosed before the target was enabled'));
  });

  it('uses a default reason and can be dismissed without closing', async () => {
    const onClose = vi.fn().mockResolvedValue({});
    render(<CloseIncident incident={open} onClose={onClose} />);

    fireEvent.click(screen.getByRole('button', { name: 'Close incident' }));
    fireEvent.click(screen.getByRole('button', { name: 'Keep open' }));
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Close incident' }));
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Azzy' } });
    fireEvent.click(screen.getByRole('button', { name: 'Close incident' }));
    await waitFor(() => expect(onClose).toHaveBeenCalledWith('Azzy', expect.stringMatching(/Closed by operator/)));
  });

  it('keeps the form open and shows the error when closing fails', async () => {
    const onClose = vi.fn().mockRejectedValue(new Error('boom'));
    render(<CloseIncident incident={open} onClose={onClose} error={new Error('The incident could not be closed.')} />);

    fireEvent.click(screen.getByRole('button', { name: 'Close incident' }));
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Azzy' } });
    fireEvent.click(screen.getByRole('button', { name: 'Close incident' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(screen.getByRole('alert')).toHaveTextContent('The incident could not be closed.');
    expect(screen.getByRole('button', { name: 'Keep open' })).toBeInTheDocument();
  });
});
