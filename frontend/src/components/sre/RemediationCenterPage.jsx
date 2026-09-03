import React, { useEffect, useMemo, useState } from 'react';
import { RemediationBadge, RiskBadge } from './Badges';
import { AsyncView } from './StateViews';
import Tabs from '../ui/Tabs';
import { useIncidentActions, useIncidents, useIncidentDetails } from '../../hooks/useIncidents';
import { formatDateTime } from '../../services/incidentService';
import { getActionLabel } from '../../lib/labels';
import { RemediationStatus } from '../../types/incident';
import { IconZap, IconShield } from '../Icons';

const GROUPS = [
  { key: 'pending', label: 'Pending approval', statuses: [RemediationStatus.AwaitingApproval] },
  { key: 'active', label: 'Active', statuses: [RemediationStatus.Approved, RemediationStatus.Executing] },
  { key: 'completed', label: 'Completed', statuses: [RemediationStatus.Executed] },
  { key: 'failed', label: 'Failed', statuses: [RemediationStatus.Failed, RemediationStatus.PolicyRejected] },
  { key: 'rejected', label: 'Rejected / cancelled', statuses: [RemediationStatus.Rejected, RemediationStatus.Cancelled] }
];

/**
 * Centralized view of remediation actions across incidents (frontend PRD section 23).
 *
 * The backend has no dedicated remediation-list endpoint - actions live on each incident - so this
 * composes the view from incident detail the same way AI Insights does, bounded for the same reason.
 */
export default function RemediationCenterPage() {
  const feed = useIncidents({ status: '', pollMs: 5000 });
  const details = useIncidentDetails(feed.incidents, { limit: 20 });
  const [group, setGroup] = useState('pending');
  const [selectedIds, setSelectedIds] = useState(() => new Set());

  const bulkActions = useIncidentActions(async () => {
    await Promise.all([details.reload({ silent: true }), feed.reload({ silent: true })]);
  });

  const actions = useMemo(() => {
    return (details.data || []).flatMap((incident) =>
      (incident.actions || []).map((action) => ({ ...action, incidentKey: incident.incidentKey, incidentId: incident.id }))
    );
  }, [details.data]);

  const grouped = useMemo(() => {
    const active = GROUPS.find((g) => g.key === group);
    return actions
      .filter((a) => active.statuses.includes(a.status))
      .sort((a, b) => new Date(b.approvedAt || b.completedAt || 0) - new Date(a.approvedAt || a.completedAt || 0));
  }, [actions, group]);

  const isPendingGroup = group === 'pending';

  // A selection only makes sense against the pending list, and only for actions still actually
  // pending - drop anything that switched group (e.g. someone else approved it) or scrolled out of
  // view rather than silently bulk-acting on stale rows.
  useEffect(() => {
    if (!isPendingGroup) {
      setSelectedIds(new Set());
      return;
    }
    const visibleIds = new Set(grouped.map((a) => a.id));
    setSelectedIds((prev) => new Set([...prev].filter((id) => visibleIds.has(id))));
    // grouped is recomputed from actions/group every render with a new array reference; keying off
    // its content (the id list) rather than the array itself avoids re-running on every poll tick.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isPendingGroup, group, grouped.map((a) => a.id).join(',')]);

  const toggleOne = (id) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleAll = () => {
    setSelectedIds((prev) => (prev.size === grouped.length ? new Set() : new Set(grouped.map((a) => a.id))));
  };

  const groupTabs = GROUPS.map((g) => ({
    id: g.key,
    label: g.label,
    count: actions.filter((a) => g.statuses.includes(a.status)).length
  }));

  const selectedActions = grouped.filter((a) => selectedIds.has(a.id));
  const [bulkBusy, setBulkBusy] = useState(false);

  const runBulk = async (work) => {
    setBulkBusy(true);
    try {
      for (const action of selectedActions) {
        await work(action);
      }
      setSelectedIds(new Set());
    } finally {
      setBulkBusy(false);
    }
  };

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconZap className="w-6 h-6 tone-healthy" />
          </div>
          <div>
            <h3>Actions</h3>
            <p className="section-desc">Every remediation action Kairon has proposed, approved and executed, in one place</p>
          </div>
        </div>

        <Tabs items={groupTabs} activeId={group} onChange={setGroup} />
      </div>

      {isPendingGroup && selectedActions.length > 0 && (
        <BulkApprovalBar
          selected={selectedActions}
          busy={bulkBusy}
          error={bulkActions.actionError}
          onApprove={(operator, note) =>
            runBulk((action) => bulkActions.approve(action.incidentId, action.id, operator, note))
          }
          onReject={(operator, note) =>
            runBulk((action) => bulkActions.reject(action.incidentId, action.id, operator, note))
          }
          onClear={() => setSelectedIds(new Set())}
        />
      )}

      <AsyncView
        query={feed}
        loadingLabel="Loading remediation actions..."
        emptyTitle="No remediation activity yet"
        emptyHint="Actions appear here once an incident reaches a recommendation. Run the incident simulation to see one."
        emptyIcon={<IconShield className="w-10 h-10" />}
      >
        {() =>
          grouped.length === 0 ? (
            <p className="panel-pending-text">Nothing in this category.</p>
          ) : (
            <div className="table-responsive">
              <table className="custom-table">
                <thead>
                  <tr>
                    {isPendingGroup && (
                      <th>
                        <input
                          type="checkbox"
                          aria-label="Select all pending actions"
                          checked={selectedIds.size > 0 && selectedIds.size === grouped.length}
                          onChange={toggleAll}
                        />
                      </th>
                    )}
                    <th>Action</th>
                    <th>Incident</th>
                    <th>Risk</th>
                    <th>Status</th>
                    <th>Operator</th>
                    <th>Time</th>
                  </tr>
                </thead>
                <tbody>
                  {grouped.map((action) => (
                    <tr key={action.id} className={selectedIds.has(action.id) ? 'row-selected' : ''}>
                      {isPendingGroup && (
                        <td>
                          <input
                            type="checkbox"
                            aria-label={`Select ${getActionLabel(action.actionType)} for ${action.incidentKey}`}
                            checked={selectedIds.has(action.id)}
                            onChange={() => toggleOne(action.id)}
                          />
                        </td>
                      )}
                      <td><code className="recommendation-action" title={action.actionType}>{getActionLabel(action.actionType)}</code></td>
                      <td><code className="path-code">{action.incidentKey}</code></td>
                      <td><RiskBadge risk={action.riskLevel} /></td>
                      <td><RemediationBadge status={action.status} /></td>
                      <td>{action.approvedBy || '--'}</td>
                      <td>
                        {action.completedAt
                          ? formatDateTime(action.completedAt)
                          : action.verificationResult
                            ? formatDateTime(action.verificationResult.completedAt)
                            : '--'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        }
      </AsyncView>
    </div>
  );
}

/**
 * Bulk approve/reject for the pending-approval group (redesign brief section 8: "bulk-select for
 * pending approvals"). Same deliberate two-step safety as the single-action ApprovalPanel - an
 * operator identity is required, and nothing runs until a second, explicit confirmation - just
 * applied to every selected action in turn rather than one at a time.
 */
function BulkApprovalBar({ selected, busy, error, onApprove, onReject, onClear }) {
  const [operator, setOperator] = useState('');
  const [confirming, setConfirming] = useState(null); // null | 'approve' | 'reject'

  const identityMissing = operator.trim().length === 0;

  const confirm = async () => {
    if (identityMissing) return;
    if (confirming === 'approve') await onApprove(operator.trim(), null);
    else await onReject(operator.trim(), 'Bulk rejection');
    setConfirming(null);
  };

  return (
    <div className="bulk-approval-bar">
      <div className="bulk-approval-summary">
        <strong>{selected.length}</strong> action{selected.length === 1 ? '' : 's'} selected
        <button type="button" className="secondary-btn" onClick={onClear} disabled={busy}>
          Clear selection
        </button>
      </div>

      {!confirming ? (
        <div className="bulk-approval-form">
          <input
            className="approval-input"
            value={operator}
            onChange={(e) => setOperator(e.target.value)}
            placeholder="Operator identity (recorded in the audit trail)"
            autoComplete="off"
            aria-label="Operator identity"
          />
          <button
            type="button"
            className="approve-btn"
            onClick={() => setConfirming('approve')}
            disabled={identityMissing || busy}
            title={identityMissing ? 'Enter your operator identity first' : undefined}
          >
            Approve selected
          </button>
          <button
            type="button"
            className="reject-btn"
            onClick={() => setConfirming('reject')}
            disabled={identityMissing || busy}
          >
            Reject selected
          </button>
        </div>
      ) : (
        <div className="approval-confirm">
          <p>
            {confirming === 'approve'
              ? `Execute all ${selected.length} selected actions?`
              : `Reject all ${selected.length} selected actions?`}
          </p>
          <div className="approval-buttons">
            <button
              type="button"
              className={confirming === 'approve' ? 'approve-btn' : 'reject-btn'}
              onClick={confirm}
              disabled={busy}
            >
              {busy ? 'Working...' : confirming === 'approve' ? 'Yes, execute all' : 'Yes, reject all'}
            </button>
            <button type="button" className="secondary-btn" onClick={() => setConfirming(null)} disabled={busy}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {error && <p className="approval-error">{error.message}</p>}
    </div>
  );
}
