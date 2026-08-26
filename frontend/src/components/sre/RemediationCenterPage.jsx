import React, { useMemo, useState } from 'react';
import { RemediationBadge, RiskBadge } from './Badges';
import { AsyncView } from './StateViews';
import { useIncidents, useIncidentDetails } from '../../hooks/useIncidents';
import { formatDateTime } from '../../services/incidentService';
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

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge">
            <IconZap className="w-6 h-6 text-emerald-500" />
          </div>
          <div>
            <h3>Remediation</h3>
            <p className="section-desc">Every remediation action Kairon has proposed, approved and executed, in one place</p>
          </div>
        </div>

        <div className="filter-pills">
          {GROUPS.map((g) => (
            <button
              key={g.key}
              type="button"
              className={`filter-pill ${group === g.key ? 'active' : ''}`}
              onClick={() => setGroup(g.key)}
            >
              {g.label} ({actions.filter((a) => g.statuses.includes(a.status)).length})
            </button>
          ))}
        </div>
      </div>

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
                    <th>Incident</th>
                    <th>Action</th>
                    <th>Risk</th>
                    <th>Status</th>
                    <th>Approved by</th>
                    <th>Executed</th>
                    <th>Verified</th>
                  </tr>
                </thead>
                <tbody>
                  {grouped.map((action) => (
                    <tr key={action.id}>
                      <td><code className="path-code">{action.incidentKey}</code></td>
                      <td><code className="recommendation-action">{action.actionType}</code></td>
                      <td><RiskBadge risk={action.riskLevel} /></td>
                      <td><RemediationBadge status={action.status} /></td>
                      <td>{action.approvedBy || '--'}</td>
                      <td>{action.completedAt ? formatDateTime(action.completedAt) : '--'}</td>
                      <td>{action.verificationResult ? formatDateTime(action.verificationResult.completedAt) : '--'}</td>
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
