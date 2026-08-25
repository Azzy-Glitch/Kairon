import React from 'react';
import { IconRefresh, IconServer, IconShield } from '../Icons';

/**
 * The four states every async view needs (frontend PRD section 19). Having them in one place is
 * what keeps "loading", "nothing to show" and "something broke" visually distinguishable
 * everywhere instead of per-screen improvisation.
 */

export function LoadingState({ label = 'Loading...' }) {
  return (
    <div className="sre-state sre-state-loading">
      <div className="sre-spinner" />
      <p>{label}</p>
    </div>
  );
}

export function EmptyState({ title = 'Nothing to show', hint, icon }) {
  return (
    <div className="sre-state sre-state-empty">
      {icon || <IconShield className="w-10 h-10" />}
      <p className="sre-state-title">{title}</p>
      {hint && <p className="sre-state-hint">{hint}</p>}
    </div>
  );
}

/**
 * Operator-facing error. It renders the normalized message from the API layer and nothing else:
 * no stack traces, no raw response bodies (frontend PRD section 18).
 */
export function ErrorState({ error, onRetry, title }) {
  const kind = error?.kind || 'error';

  const heading =
    title ||
    (kind === 'offline'
      ? 'Backend unreachable'
      : kind === 'timeout'
        ? 'Request timed out'
        : kind === 'unauthorized'
          ? 'Not authorized'
          : kind === 'not-found'
            ? 'Not found'
            : 'Something went wrong');

  return (
    <div className="sre-state sre-state-error">
      <IconServer className="w-10 h-10" />
      <p className="sre-state-title">{heading}</p>
      <p className="sre-state-hint">{error?.message || 'The request failed.'}</p>
      {onRetry && (
        <button type="button" className="secondary-btn" onClick={onRetry}>
          <IconRefresh className="w-4 h-4 mr-1" />
          Retry
        </button>
      )}
    </div>
  );
}

/**
 * Renders whichever state applies, so screens do not each re-implement the same four branches.
 * `children` is only called on success.
 */
export function AsyncView({ query, loadingLabel, emptyTitle, emptyHint, emptyIcon, onRetry, children }) {
  if (query.isLoading && !query.data) return <LoadingState label={loadingLabel} />;
  if (query.isError && !query.data) {
    return <ErrorState error={query.error} onRetry={onRetry || (() => query.reload())} />;
  }
  if (query.isEmpty) return <EmptyState title={emptyTitle} hint={emptyHint} icon={emptyIcon} />;

  return children(query.data);
}

/**
 * A quiet banner for a background refresh that failed while stale data is still on screen. The
 * operator needs to know the numbers are not live, but blanking the dashboard would be worse.
 */
export function StaleBanner({ error }) {
  if (!error) return null;

  return (
    <div className="sre-stale-banner">
      <span className="sre-stale-dot" />
      Showing last known data. {error.message}
    </div>
  );
}
