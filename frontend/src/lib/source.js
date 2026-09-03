/**
 * .NET / Python SDK source separation (redesign brief section 6) - entirely client-side, no
 * backend field added.
 *
 * Reality check on the data, from reading the actual backend source rather than guessing: a
 * formal link from telemetry to a known SdkType exists (MonitoredApplication.Runtime /
 * SdkInstallation.SdkType), but it is only populated by the newer normalized telemetry pipeline
 * (PlatformTelemetryService) - neither SDK's current KaironTelemetryClient.SendAsync /
 * kairon.client.Kairon._send actually posts through that pipeline yet, they both still use the
 * older api/telemetry/incidents endpoint. So most real incident/telemetry rows today carry no
 * formal SdkType at all.
 *
 * resolveSource therefore tries the formal link first (for data that does have it, e.g. once the
 * SDKs move to the newer pipeline or for anything created through the installation-issuing flow),
 * and falls back to a same-app naming heuristic on Application/Service otherwise - the one field
 * that is reliably present on every row today, because it is the SDK's own ApplicationName /
 * ServiceName config value. Machine-agent data (the Machines page) is never inferred this way -
 * its own data shape (Machine/DiscoveredApplication records, no Application/Service telemetry
 * fields at all) makes it unambiguous, so callers on that page pass 'agent' directly.
 */

import { IconTerminal, IconDashboard, IconServer, IconAlertTriangle } from '../components/Icons';

export const SDK_SOURCES = ['dotnet', 'python', 'agent', 'unknown'];

export const sourceMeta = {
  dotnet: { label: '.NET app', tint: 'var(--series-1)', icon: IconTerminal },
  python: { label: 'Python app', tint: 'var(--series-2)', icon: IconDashboard },
  agent: { label: 'Machine agent', tint: 'var(--series-3)', icon: IconServer },
  unknown: { label: 'Unknown source', tint: 'var(--neutral)', icon: IconAlertTriangle }
};

function normalizeSdkType(value) {
  if (!value) return null;
  const v = String(value).toLowerCase();
  if (v.includes('dotnet') || v === '.net' || v.includes('csharp') || v.includes('c#')) return 'dotnet';
  if (v.includes('python') || v === 'py') return 'python';
  return null;
}

/** Same-app naming heuristic: the SDK's own ApplicationName/ServiceName config value is the one
 * field reliably present on every row today. Looks at both Application and Service since callers
 * set either or both. */
function heuristicFromNameFields(record) {
  const haystack = `${record?.application ?? record?.Application ?? ''} ${record?.service ?? record?.Service ?? ''}`.toLowerCase();
  if (!haystack.trim()) return 'unknown';
  if (haystack.includes('dotnet') || haystack.includes('.net')) return 'dotnet';
  if (haystack.includes('python')) return 'python';
  return 'unknown';
}

/**
 * resolveSource(record, installationsByApplicationName?)
 *
 * record: an Incident/Metric/telemetry-shaped object (camelCase or PascalCase fields tolerated).
 * installationsByApplicationName: optional Map<applicationName, sdkType> built from
 * GET /api/v1/platform/sdk-installations + /api/v1/platform/applications (both operator-gated, so
 * only available when the caller has that access) - when supplied and a match is found, this is
 * authoritative and skips the heuristic entirely.
 */
export function resolveSource(record, installationsByApplicationName) {
  if (!record) return 'unknown';

  // Explicit source already resolved/attached by the caller (e.g. the Machines page passing
  // 'agent' directly) - always wins.
  if (record.__source && SDK_SOURCES.includes(record.__source)) return record.__source;

  const appName = record.application ?? record.Application ?? record.service ?? record.Service;
  if (installationsByApplicationName && appName) {
    const matched = normalizeSdkType(installationsByApplicationName.get(appName));
    if (matched) return matched;
  }

  return heuristicFromNameFields(record);
}

/** Builds the installationsByApplicationName map from the two operator-gated endpoints' real
 * response shapes (Applications: {Id, Name, ...}; Installations: {ApplicationId, SdkType, ...}). */
export function buildInstallationLookup(applications, installations) {
  const nameById = new Map((applications || []).map((a) => [a.id ?? a.Id, a.name ?? a.Name]));
  const lookup = new Map();
  for (const inst of installations || []) {
    const appId = inst.applicationId ?? inst.ApplicationId;
    const name = nameById.get(appId);
    if (name) lookup.set(name, inst.sdkType ?? inst.SdkType);
  }
  return lookup;
}

/** Counts records by resolved source, for the "All sources (18) - .NET (16) - Python (2)" filter
 * row (brief section 6). */
export function countBySource(records, installationsByApplicationName) {
  const counts = { all: records.length, dotnet: 0, python: 0, agent: 0, unknown: 0 };
  for (const record of records) {
    counts[resolveSource(record, installationsByApplicationName)] += 1;
  }
  return counts;
}
