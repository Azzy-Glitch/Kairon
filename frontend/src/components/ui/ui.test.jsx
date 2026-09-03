import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import Button from './Button';
import Badge from './Badge';
import Card from './Card';
import MetricTile from './MetricTile';
import DataTable from './DataTable';
import EmptyState from './EmptyState';
import StatusTimeline from './StatusTimeline';
import Tabs from './Tabs';

describe('Button', () => {
  it('renders every variant without throwing and fires onClick', async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(
      <>
        <Button variant="primary" onClick={onClick}>Approve and run</Button>
        <Button variant="secondary">Cancel</Button>
        <Button variant="ghost">Dismiss</Button>
        <Button variant="danger">Reject</Button>
      </>
    );
    await user.click(screen.getByText('Approve and run'));
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('disabled button does not fire onClick', async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<Button onClick={onClick} disabled>Approve and run</Button>);
    await user.click(screen.getByText('Approve and run'));
    expect(onClick).not.toHaveBeenCalled();
  });
});

describe('Badge', () => {
  it('falls back to neutral tone for an unknown tone value', () => {
    render(<Badge tone="not-a-real-tone">Critical</Badge>);
    expect(screen.getByText('Critical').className).toMatch(/ui-badge-neutral/);
  });
});

describe('Card', () => {
  it('renders title, badge and action in the header slot', () => {
    render(
      <Card title="Service degradation" badge={<Badge tone="critical">Critical</Badge>} action={<button>Open</button>} accentSide="critical">
        Body content
      </Card>
    );
    expect(screen.getByText('Service degradation')).toBeInTheDocument();
    expect(screen.getByText('Critical')).toBeInTheDocument();
    expect(screen.getByText('Open')).toBeInTheDocument();
    expect(screen.getByText('Body content')).toBeInTheDocument();
  });
});

describe('MetricTile', () => {
  it('shows an empty state with no samples', () => {
    render(<MetricTile label="CPU usage" value={null} data={[]} />);
    expect(screen.getByText('No data yet')).toBeInTheDocument();
  });

  it('marks the tile breached when the value exceeds threshold', () => {
    render(<MetricTile label="CPU usage" value={95} unit="%" threshold={80} data={[{ t: '10:00', v: 95 }]} />);
    expect(screen.getByText('95%')).toBeInTheDocument();
    expect(screen.getByLabelText('Over threshold')).toBeInTheDocument();
  });
});

describe('DataTable', () => {
  const columns = [
    { key: 'name', label: 'Service', priority: 0 },
    { key: 'latency', label: 'Latency', align: 'right', mono: true, priority: 1, sortable: true }
  ];
  const rows = [
    { id: '1', name: 'OrderService', latency: 120 },
    { id: '2', name: 'PaymentService', latency: 340 }
  ];

  it('renders rows and sorts on header click', async () => {
    const user = userEvent.setup();
    render(<DataTable columns={columns} rows={rows} getRowKey={(r) => r.id} />);
    expect(screen.getByText('OrderService')).toBeInTheDocument();
    await user.click(screen.getByText('Latency'));
    // /.+Service$/ (not just /Service$/) so this doesn't also match the "Service" column header.
    const cells = screen.getAllByText(/.+Service$/);
    expect(cells[0]).toHaveTextContent('PaymentService');
  });

  it('shows the provided empty state when there are no rows', () => {
    render(<DataTable columns={columns} rows={[]} getRowKey={(r) => r.id} emptyState={<div>Nothing here</div>} />);
    expect(screen.getByText('Nothing here')).toBeInTheDocument();
  });
});

describe('EmptyState', () => {
  it('renders a description and an action', () => {
    render(<EmptyState title="No telemetry yet" description="Connect an app to start collecting." action={<button>Connect an app</button>} />);
    expect(screen.getByText('No telemetry yet')).toBeInTheDocument();
    expect(screen.getByText('Connect an app')).toBeInTheDocument();
  });
});

describe('StatusTimeline', () => {
  const steps = [
    { key: 'detected', label: 'Detected' },
    { key: 'investigating', label: 'Investigating' },
    { key: 'resolved', label: 'Resolved' }
  ];

  it('marks steps before currentKey as completed and after as future', () => {
    render(<StatusTimeline steps={steps} currentKey="investigating" />);
    const items = screen.getAllByRole('listitem');
    expect(items[0].className).toMatch(/completed/);
    expect(items[1].className).toMatch(/current/);
    expect(items[2].className).toMatch(/future/);
  });
});

describe('Tabs', () => {
  it('shows counts and calls onChange with the clicked id', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(
      <Tabs
        items={[
          { id: 'pending', label: 'Pending approval', count: 3 },
          { id: 'active', label: 'Active', count: 1 }
        ]}
        activeId="pending"
        onChange={onChange}
      />
    );
    expect(screen.getByText('(3)')).toBeInTheDocument();
    await user.click(screen.getByText('Active'));
    expect(onChange).toHaveBeenCalledWith('active');
  });
});
