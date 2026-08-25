import React, { useState, useEffect } from 'react';
import { devopsApi } from '../api/index';
import { IconHistory, IconTrash, IconRefresh, IconBug, IconLink, IconPredict, IconSparkles, IconCopy, IconCheck } from './Icons';
import { useToast } from './Toast';

export default function AuditHistory() {
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
      case 'error': return <IconBug className="w-4 h-4 text-rose-600" />;
      case 'api': return <IconLink className="w-4 h-4 text-sky-600" />;
      case 'predict': return <IconPredict className="w-4 h-4 text-purple-600" />;
      default: return <IconSparkles className="w-4 h-4 text-amber-600" />;
    }
  };

  const filteredHistory = filter === 'all'
    ? history
    : history.filter(item => item.type === filter);

  return (
    <div className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge history-badge">
            <IconHistory className="w-6 h-6 text-emerald-600" />
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

      <div className="filter-pills">
        {['all', 'error', 'api', 'predict', 'recommend'].map(f => (
          <button
            key={f}
            className={`filter-pill ${filter === f ? 'active' : ''}`}
            onClick={() => setFilter(f)}
          >
            {f === 'all' ? 'All Records' : f.toUpperCase()}
          </button>
        ))}
      </div>

      {filteredHistory.length === 0 ? (
        <div className="empty-state">
          <IconHistory className="w-12 h-12 text-slate-400 mb-3" />
          <h4>No Telemetry Records Found</h4>
          <p>Run diagnostics on stack traces, APIs, or reliability forecasts to populate the persistent database.</p>
        </div>
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
              <tbody>
                {filteredHistory.map((item) => (
                  <tr
                    key={item.id}
                    className={selectedItem?.id === item.id ? 'row-selected' : ''}
                    onClick={() => setSelectedItem(item)}
                  >
                    <td>
                      <span className="type-pill">
                        {getTypeIcon(item.type)}
                        <span className="ml-1 uppercase">{item.type}</span>
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
                <span><strong>Type:</strong> {selectedItem.type.toUpperCase()}</span>
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