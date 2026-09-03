import React, { useState, useEffect, useMemo } from 'react';
import { devopsApi } from '../api/index';
import { IconHistory, IconTrash, IconRefresh, IconBug, IconLink, IconPredict, IconSparkles, IconCopy, IconCheck } from './Icons';
import { useToast } from './Toast';
import Tabs from './ui/Tabs';
import EmptyState from './ui/EmptyState';
import Button from './ui/Button';

const TYPE_LABEL = { error: 'Error Analyzer', api: 'API Validator', predict: 'Predictor', recommend: 'Recommender' };
const typeLabel = (type) => TYPE_LABEL[type] || type;

function dayLabel(isoString) {
  const date = new Date(isoString);
  const today = new Date();
  const yesterday = new Date();
  yesterday.setDate(today.getDate() - 1);

  if (date.toDateString() === today.toDateString()) return 'Today';
  if (date.toDateString() === yesterday.toDateString()) return 'Yesterday';
  return date.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' });
}

/** Groups already-sorted (newest first) records into same-day buckets, preserving order. */
function groupByDay(records) {
  const groups = [];
  let current = null;
  for (const record of records) {
    const label = dayLabel(record.createdAt);
    if (!current || current.label !== label) {
      current = { label, records: [] };
      groups.push(current);
    }
    current.records.push(record);
  }
  return groups;
}

export default function AuditHistory({ onOpenDiagnostics }) {
  const [history, setHistory] = useState([]);
  const [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState('all');
  const [selectedItem, setSelectedItem] = useState(null);
  const { addToast } = useToast();

  const fetchHistory = async () => {
    setLoading(true);
    try {
      const data = await devopsApi.getHistory();
      setHistory(data || []);
    } catch (e) {
      addToast('Failed to fetch history', 'warning');
    } finally {
      setLoading(false);
    }
  };

  const clearHistory = async () => {
    if (!window.confirm('Are you sure you want to clear all telemetry history?')) return;
    try {
      await devopsApi.clearHistory();
      setHistory([]);
      setSelectedItem(null);
      addToast('Incident audit log cleared', 'info');
    } catch (e) {
      addToast('Failed to clear history', 'error');
    }
  };

  useEffect(() => {
    fetchHistory();
  }, []);

  const getTypeIcon = (type) => {
    switch (type) {
      case 'error': return <IconBug className="w-4 h-4 tone-critical" />;
      case 'api': return <IconLink className="w-4 h-4 tone-low" />;
      case 'predict': return <IconPredict className="w-4 h-4 tone-accent" />;
      default: return <IconSparkles className="w-4 h-4 tone-medium" />;
    }
  };

  const filteredHistory = filter === 'all'
    ? history
    : history.filter(item => item.type === filter);

  const dayGroups = useMemo(() => groupByDay(filteredHistory), [filteredHistory]);

  const filterItems = [
    { id: 'all', label: 'All records', count: history.length },
    ...['error', 'api', 'predict', 'recommend']
      .map((id) => ({ id, label: typeLabel(id), count: history.filter((h) => h.type === id).length }))
      .filter((item) => item.count > 0)
  ];

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge history-badge">
            <IconHistory className="w-6 h-6 tone-healthy" />
          </div>
          <div>
            <h3>Incident Telemetry & Audit Trail</h3>
            <p className="section-desc">Persistent SQLite database records of all diagnosed incidents, schema audits, and risk forecasts</p>
          </div>
        </div>

        <div className="flex-actions">
          <button className="secondary-btn" onClick={fetchHistory} disabled={loading}>
            <IconRefresh className={`w-4 h-4 mr-1 ${loading ? 'animate-spin' : ''}`} />
            Refresh
          </button>
          {history.length > 0 && (
            <button className="danger-btn" onClick={clearHistory}>
              <IconTrash className="w-4 h-4 mr-1" />
              Clear Log
            </button>
          )}
        </div>
      </div>

      <Tabs items={filterItems} activeId={filter} onChange={setFilter} className="audit-filter-tabs" />

      {filteredHistory.length === 0 ? (
        <EmptyState
          icon={<IconHistory className="w-12 h-12" />}
          title="No audit history yet"
          description="Every diagnostic you run - error analysis, API validation, predictions - is recorded here."
          action={
            onOpenDiagnostics ? (
              <Button variant="primary" onClick={onOpenDiagnostics}>
                Open diagnostics
              </Button>
            ) : undefined
          }
        />
      ) : (
        <div className="history-grid">
          <div className="history-table-wrapper">
            <table className="custom-table">
              <thead>
                <tr>
                  <th>Type</th>
                  <th>Input Preview</th>
                  <th>Score</th>
                  <th>Timestamp</th>
                  <th>Action</th>
                </tr>
              </thead>
              {dayGroups.map((group) => (
                <tbody key={group.label}>
                  <tr className="history-day-heading-row">
                    <td colSpan={5}>{group.label}</td>
                  </tr>
                  {group.records.map((item) => (
                    <tr
                      key={item.id}
                      className={selectedItem?.id === item.id ? 'row-selected' : ''}
                      onClick={() => setSelectedItem(item)}
                    >
                      <td>
                        <span className="type-pill">
                          {getTypeIcon(item.type)}
                          <span className="ml-1">{typeLabel(item.type)}</span>
                        </span>
                      </td>
                      <td>
                        <div className="truncate-text">{item.input || 'Empty payload'}</div>
                      </td>
                      <td>
                        <span className="score-badge-sm">{item.score}/100</span>
                      </td>
                      <td className="timestamp-cell">
                        {new Date(item.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                      </td>
                      <td>
                        <button
                          className="small-inspect-btn"
                          onClick={(e) => { e.stopPropagation(); setSelectedItem(item); }}
                        >
                          Inspect
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              ))}
            </table>
          </div>

          {selectedItem && (
            <div className="history-details-card animate-fade-in">
              <div className="details-header">
                <h4>
                  {getTypeIcon(selectedItem.type)}
                  <span className="ml-2">Record #{selectedItem.id} Details</span>
                </h4>
                <button className="close-btn" onClick={() => setSelectedItem(null)}>✕</button>
              </div>
              <div className="details-meta">
                <span><strong>Type:</strong> {typeLabel(selectedItem.type)}</span>
                <span><strong>Recorded:</strong> {new Date(selectedItem.createdAt).toLocaleString()}</span>
                <span><strong>Score:</strong> {selectedItem.score}/100</span>
              </div>
              <div className="details-block">
                <span className="block-label">Input Payload:</span>
                <pre className="json-pre">{selectedItem.input}</pre>
              </div>
              <div className="details-block">
                <span className="block-label">Diagnostic Output:</span>
                <pre className="json-pre">
                  {(() => {
                    try {
                      return JSON.stringify(JSON.parse(selectedItem.outputJson), null, 2);
                    } catch {
                      return selectedItem.outputJson || 'No output recorded';
                    }
                  })()}
                </pre>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}