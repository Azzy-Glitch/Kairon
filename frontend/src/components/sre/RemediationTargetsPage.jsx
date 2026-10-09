import React, { useEffect, useMemo, useRef, useState } from 'react';
import { remediationTargetsApi, sdkApi, agentApi } from '../../api';
import { useToast } from '../Toast';
import DataTable from '../ui/DataTable';
import EmptyState from '../ui/EmptyState';
import Button from '../ui/Button';
import Badge from '../ui/Badge';
import StatusTimeline from '../ui/StatusTimeline';
import { RiskBadge } from './Badges';
import { CodeBlock } from './CopyableCode';
import { IconZap, IconAlertTriangle } from '../Icons';
import { OPERATIONS, APP_OPERATIONS, TARGET_KINDS, describeReadiness, operationLabel } from '../../lib/remediation';

const ENVIRONMENTS = ['Development', 'Staging', 'Production'];

const WIZARD_STEPS = [
  { key: 'app', label: 'Project & service' },
  { key: 'machine', label: 'Machine' },
  { key: 'service', label: 'Windows service' },
  { key: 'operations', label: 'Operations' },
  { key: 'permission', label: 'Permission check' },
  { key: 'review', label: 'Review & enable' }
];

/** Restarting the connected app itself needs no Windows service and no permission grant: the
 * KAIRON UserAgent restarts it as its own user. Its check step confirms the app is identified. */
const APP_WIZARD_STEPS = [
  { key: 'app', label: 'Project & service' },
  { key: 'machine', label: 'Machine' },
  { key: 'operations', label: 'Operations' },
  { key: 'permission', label: 'Readiness check' },
  { key: 'review', label: 'Review & enable' }
];

export function wizardSteps(kind) {
  return kind === 'AppProcess' ? APP_WIZARD_STEPS : WIZARD_STEPS;
}

function defaultOperations(kind) {
  return kind === 'AppProcess' ? ['RestartApplication'] : ['RunHealthCheck'];
}

function programName(path) {
  return path ? path.split(/[\\/]/).pop() : null;
}

// Pre-flight check keys -> plain wording, used only when the backend sends no label of its own.
const CHECK_FALLBACK_LABELS = {
  'agent-proof': 'App telemetry confirmed by the Agent',
  identity: 'Windows service is the same one that was confirmed',
  process: 'KAIRON knows which process is the application',
  useragent: "The KAIRON UserAgent in the app's user session can restart it",
  'eligible-process': 'The process is an ordinary application'
};

/** Safe defaults: Development, the only enrolled machine if there is exactly one, and the least
 * powerful operation (a read-only health check). Everything else is opt-in. */
function emptyForm(machines = []) {
  const onlyMachine = machines.length === 1 ? machines[0] : null;
  return {
    id: null,
    projectId: '',
    environment: 'Development',
    service: '',
    telemetryCredentialId: '',
    machineId: onlyMachine?.id || '',
    expectedHostName: onlyMachine?.hostName || '',
    kind: 'AppProcess',
    windowsServiceName: '',
    allowedOperations: defaultOperations('AppProcess'),
    updatedAt: null
  };
}

function formFromTarget(target) {
  return {
    id: target.id,
    projectId: target.projectId,
    environment: target.environment,
    service: target.service,
    telemetryCredentialId: target.telemetryCredentialId,
    machineId: target.machineId,
    expectedHostName: target.expectedHostName,
    kind: target.kind || 'WindowsService',
    windowsServiceName: target.windowsServiceName || '',
    allowedOperations: target.allowedOperations || [],
    updatedAt: target.updatedAt
  };
}

/** A request is only ever sent once every required field is actually filled in - an incomplete
 * form must never reach the API as empty GUIDs. */
export function isPayloadComplete(form) {
  return Boolean(form && form.projectId && form.environment && form.service?.trim() && form.machineId &&
    form.telemetryCredentialId && (form.kind === 'AppProcess' || form.windowsServiceName?.trim()) &&
    form.allowedOperations?.length > 0);
}

function buildPayload(form, enabled) {
  return {
    projectId: form.projectId,
    environment: form.environment,
    service: form.service.trim(),
    machineId: form.machineId,
    telemetryCredentialId: form.telemetryCredentialId,
    expectedHostName: form.expectedHostName,
    kind: form.kind || 'WindowsService',
    windowsServiceName: form.kind === 'AppProcess' ? '' : form.windowsServiceName.trim(),
    allowedOperations: form.allowedOperations,
    enabled
  };
}

function errorMessages(err, fallback) {
  if (err?.code === 'stale-update') {
    return ['This target was changed by another operator. Reload the latest version before editing it again.'];
  }
  if (err?.fieldErrors?.length) return err.fieldErrors;
  return [err?.message || fallback];
}

/**
 * Operator-facing management UI for remediation targets (backend/Controllers/
 * RemediationTargetsController.cs). Configuration only: nothing here executes, approves or bypasses
 * the runtime resolver's own revalidation at execution time.
 *
 * The main list speaks in plain language (service, machine, status); IDs, fingerprints and raw
 * readiness codes live under "Advanced details" in a target's View. Creating and editing use one
 * guided wizard whose permission step runs the backend's read-only Windows pre-flight.
 *
 * preselectMachineId: set by MachinesPage's "Configure remediation target" shortcut - opens the
 * wizard with that machine already selected.
 */
export default function RemediationTargetsPage({ preselectMachineId }) {
  const toast = useToast();
  const [targets, setTargets] = useState(null);
  const [loadError, setLoadError] = useState(null);
  const [projects, setProjects] = useState([]);
  const [machines, setMachines] = useState([]);
  const [wizard, setWizard] = useState(null); // null | { form, startStep }
  const [viewId, setViewId] = useState(null);
  const [confirmDisableId, setConfirmDisableId] = useState(null);
  const [busyId, setBusyId] = useState(null);

  const load = async () => {
    try {
      const list = await remediationTargetsApi.list();
      setTargets(list || []);
      setLoadError(null);
    } catch (err) {
      setLoadError(err || { message: 'Remediation targets are unavailable.' });
    }
  };

  useEffect(() => {
    load();
    sdkApi.listProjects().then(setProjects).catch(() => {});
    agentApi.getMachines().then(setMachines).catch(() => {});
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (preselectMachineId) {
      setViewId(null);
      setWizard({ form: { ...emptyForm(), machineId: preselectMachineId } });
    }
    // Only ever react to the shortcut actually changing which machine it points at.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preselectMachineId]);

  const openCreate = () => {
    setViewId(null);
    setWizard({ form: emptyForm(machines) });
  };

  const openEdit = (target) => {
    setViewId(null);
    setWizard({ form: formFromTarget(target) });
  };

  const closeWizard = () => setWizard(null);

  const handleSaved = (message) => {
    toast.addToast(message, 'success');
    setWizard(null);
    load();
  };

  const handleDisable = async (id) => {
    setBusyId(id);
    try {
      await remediationTargetsApi.disable(id);
      toast.addToast('Target disabled.', 'info');
      setConfirmDisableId(null);
      load();
    } catch (err) {
      toast.addToast(err?.message || 'Could not disable the target.', 'error');
    } finally {
      setBusyId(null);
    }
  };

  const handleEnable = async (id) => {
    setBusyId(id);
    try {
      await remediationTargetsApi.enable(id);
      toast.addToast('Target enabled.', 'success');
      load();
    } catch (err) {
      // Enabling re-runs the full validation and the Windows pre-flight server-side - a disabled
      // target can legitimately fail here even though it saved fine while disabled.
      toast.addToast(
        `${err?.message || 'Could not enable the target - it may no longer be valid.'} Open View to see what is missing.`,
        'error'
      );
    } finally {
      setBusyId(null);
    }
  };

  const viewTarget = viewId ? (targets || []).find((t) => t.id === viewId) : null;

  const columns = [
    {
      key: 'windowsServiceName',
      label: 'Restarts',
      sortable: true,
      render: (t) => (t.kind === 'AppProcess' ? (
        <span className="remediation-target-service">
          <strong>The app itself</strong>
          <span className="remediation-target-subtle">{programName(t.processExecutable) || 'process not identified yet'}</span>
        </span>
      ) : (
        <span className="remediation-target-service">
          <strong>{t.serviceDisplayName || t.windowsServiceName}</strong>
          {t.serviceDisplayName && t.serviceDisplayName !== t.windowsServiceName && (
            <span className="remediation-target-subtle">{t.windowsServiceName}</span>
          )}
          <span className="remediation-target-subtle">Windows service</span>
        </span>
      ))
    },
    { key: 'machineHostName', label: 'Machine', sortable: true, render: (t) => t.machineHostName || t.expectedHostName || 'Unknown machine' },
    { key: 'environment', label: 'Environment', sortable: true, priority: 1 },
    {
      key: 'service',
      label: 'Logical service',
      sortable: true,
      priority: 1,
      render: (t) => (
        <span className="remediation-target-service">
          <span>{t.service}</span>
          {t.projectName && <span className="remediation-target-subtle">{t.projectName}</span>}
        </span>
      )
    },
    { key: 'readiness', label: 'Status', render: (t) => <ReadinessBadge target={t} /> },
    {
      key: 'allowedOperations',
      label: 'Operations',
      priority: 2,
      render: (t) => (t.allowedOperations || []).map(operationLabel).join(', ')
    },
    {
      key: 'actions',
      label: '',
      render: (t) => (
        <span className="remediation-target-actions">
          <Button size="compact" variant="ghost" onClick={() => { setWizard(null); setViewId(t.id); }}>View</Button>
          <Button size="compact" variant="ghost" onClick={() => openEdit(t)}>Edit</Button>
          {t.enabled ? (
            confirmDisableId === t.id ? (
              <>
                <Button size="compact" variant="danger" disabled={busyId === t.id} onClick={() => handleDisable(t.id)}>
                  Confirm disable
                </Button>
                <Button size="compact" variant="ghost" onClick={() => setConfirmDisableId(null)}>Cancel</Button>
              </>
            ) : (
              <Button size="compact" variant="ghost" onClick={() => setConfirmDisableId(t.id)}>Disable</Button>
            )
          ) : (
            <Button size="compact" variant="secondary" disabled={busyId === t.id} onClick={() => handleEnable(t.id)}>
              Enable
            </Button>
          )}
        </span>
      )
    }
  ];

  return (
    <div className="animate-fade-in">
      <section className="section-card">
        <div className="section-header">
          <div className="section-title-group">
            <div className="section-icon-badge"><IconZap className="w-6 h-6 tone-neutral" /></div>
            <div>
              <h3>Remediation Targets</h3>
              <p className="section-desc">
                What KAIRON may restart for each application - the app itself, or a Windows service - on which machine,
                and which operations it may run. Every action still needs an operator's approval.
              </p>
            </div>
          </div>
          {!wizard && <Button variant="primary" onClick={openCreate}>+ Configure Target</Button>}
        </div>

        {loadError ? (
          <div>
            <p className="panel-pending-text">{loadError.message || 'Remediation targets are unavailable.'}</p>
            <Button variant="secondary" onClick={load}>Retry</Button>
          </div>
        ) : targets === null ? (
          <p className="panel-pending-text">Loading remediation targets...</p>
        ) : targets.length === 0 ? (
          <EmptyState
            icon={<IconZap className="w-10 h-10" />}
            title="No remediation targets yet"
            description="Configure one to let KAIRON restart a connected application (no setup needed), or health-check, start, restart or stop a specific Windows service."
            action={!wizard && <Button variant="primary" onClick={openCreate}>+ Configure Target</Button>}
          />
        ) : (
          <DataTable columns={columns} rows={targets} getRowKey={(t) => t.id} sortableDefaultKey="windowsServiceName" />
        )}
      </section>

      {viewTarget && !wizard && (
        <TargetDetail target={viewTarget} onClose={() => setViewId(null)} onEdit={() => openEdit(viewTarget)} />
      )}

      {wizard && (
        <TargetWizard
          key={wizard.form.id || 'new'}
          initialForm={wizard.form}
          projects={projects}
          machines={machines}
          onCancel={closeWizard}
          onSaved={handleSaved}
        />
      )}
    </div>
  );
}

function ReadinessBadge({ target }) {
  const readiness = target.enabled ? target.readiness : 'Disabled';
  const info = describeReadiness(readiness);
  return (
    <span title={info.explanation || undefined}>
      <Badge tone={info.tone}>{info.label}</Badge>
    </span>
  );
}

// ---- Saved-target detail ------------------------------------------------------------------

function TargetDetail({ target, onClose, onEdit }) {
  const [preflight, setPreflight] = useState({ loading: true, result: null, error: null });
  const info = describeReadiness(target.enabled ? target.readiness : 'Disabled');

  const runPreflight = async () => {
    setPreflight((prev) => ({ ...prev, loading: true, error: null }));
    try {
      const result = await remediationTargetsApi.targetPreflight(target.id);
      setPreflight({ loading: false, result, error: null });
    } catch (err) {
      setPreflight({ loading: false, result: null, error: err });
    }
  };

  useEffect(() => { runPreflight(); }, [target.id]); // eslint-disable-line react-hooks/exhaustive-deps

  const svc = preflight.result?.service;

  return (
    <section className="section-card" aria-labelledby="remediation-target-detail-title">
      <div className="section-header">
        <div className="section-title-group">
          <div>
            <h3 id="remediation-target-detail-title">
              {target.kind === 'AppProcess' ? `${target.service} (the app itself)` : target.serviceDisplayName || target.windowsServiceName}
            </h3>
            <p className="section-desc">
              on {target.machineHostName || target.expectedHostName || 'an unknown machine'} · {target.environment}
            </p>
          </div>
        </div>
        <span className="remediation-target-actions">
          <Button size="compact" variant="secondary" onClick={onEdit}>Edit</Button>
          <Button size="compact" variant="ghost" onClick={onClose}>Close</Button>
        </span>
      </div>

      <dl className="remediation-target-summary">
        <div><dt>Status</dt><dd><Badge tone={info.tone}>{info.label}</Badge> <span className="remediation-target-subtle">{info.explanation}</span></dd></div>
        {target.readinessDetail && <div><dt>Why</dt><dd>{target.readinessDetail}</dd></div>}
        <div><dt>Restarts</dt><dd>{TARGET_KINDS[target.kind || 'WindowsService']?.label}</dd></div>
        {target.kind === 'AppProcess' ? (
          <div>
            <dt>Application</dt>
            <dd>
              {target.processExecutable
                ? <>{target.processExecutable}{target.processWorkingDirectory ? <> in <code className="path-code">{target.processWorkingDirectory}</code></> : null}</>
                : 'Not identified yet - run the app and send it a request.'}
            </dd>
          </div>
        ) : (
          <div><dt>Windows service</dt><dd>{target.serviceDisplayName ? `${target.serviceDisplayName} (${target.windowsServiceName})` : target.windowsServiceName}</dd></div>
        )}
        <div><dt>Project</dt><dd>{target.projectName || 'Unknown project'}</dd></div>
        <div><dt>Logical service</dt><dd>{target.service}</dd></div>
        <div><dt>App credential</dt><dd>{target.telemetryCredentialName || 'Unknown credential'}</dd></div>
        <div><dt>Operations</dt><dd>{(target.allowedOperations || []).map(operationLabel).join(', ') || 'None'}</dd></div>
      </dl>

      <h4 className="remediation-target-subheading">Readiness checks</h4>
      {preflight.loading && !preflight.result ? (
        <p className="panel-pending-text">Checking this target...</p>
      ) : preflight.error ? (
        <p className="panel-pending-text">{preflight.error.message || 'Could not run the checks.'}</p>
      ) : (
        <PreflightResult result={preflight.result} serviceName={target.windowsServiceName} />
      )}
      <Button size="compact" variant="secondary" disabled={preflight.loading} onClick={runPreflight}>
        {preflight.loading ? 'Checking...' : 'Re-check'}
      </Button>

      <details className="sdk-guide-details remediation-target-advanced">
        <summary>Advanced details</summary>
        <dl className="remediation-target-summary">
          <TechRow label="Target ID" value={target.id} />
          <TechRow label="Project ID" value={target.projectId} />
          <TechRow label="Machine ID" value={target.machineId} />
          <TechRow label="Credential ID" value={target.telemetryCredentialId} />
          <TechRow label="Expected host name" value={target.expectedHostName} />
          <TechRow label="Raw readiness" value={target.readiness} />
          <TechRow label="Machine binding" value={target.machineBindingStatus} />
          <TechRow label="Last Agent confirmation" value={target.machineBindingLastConfirmedAt ? new Date(target.machineBindingLastConfirmedAt).toLocaleString() : null} />
          <TechRow label="Service identity confirmed" value={target.serviceIdentityConfirmed ? 'Yes' : 'No'} />
          <TechRow label="Executor account" value={preflight.result?.executorAccount} />
          <TechRow label="Executor SID" value={preflight.result?.executorSid} />
          <TechRow label="Service state" value={svc?.state} />
          <TechRow label="Image path" value={svc?.imagePath} />
          <TechRow label="Start account" value={svc?.startAccount} />
          <TechRow label="Eligibility" value={svc ? `${svc.eligibility}${svc.eligibilityDetail ? ` - ${svc.eligibilityDetail}` : ''}` : null} />
          <TechRow label="SCM error code" value={svc?.win32Error != null ? String(svc.win32Error) : null} />
          <TechRow label="Last updated" value={target.updatedAt ? new Date(target.updatedAt).toLocaleString() : null} />
        </dl>
      </details>
    </section>
  );
}

function TechRow({ label, value }) {
  if (value === null || value === undefined || value === '') return null;
  return <div><dt>{label}</dt><dd><code className="path-code">{value}</code></dd></div>;
}

/**
 * Renders a pre-flight result: every check with ✓/✗, the exact rights KAIRON needs (and what it
 * will never get), and the exact elevated command that grants only those rights. Shared by the
 * wizard's permission step and a saved target's View.
 */
export function PreflightResult({ result, serviceName }) {
  if (!result) return null;
  const checks = result.checks || [];
  const missing = result.missingRights || [];
  const required = result.requiredRights || [];
  const name = result.service?.displayName || result.service?.serviceName || serviceName || 'this service';

  return (
    <div className="remediation-preflight">
      <ul className="remediation-preflight-checks">
        {checks.map((check) => (
          <li key={check.key} className={`remediation-preflight-check ${check.passed ? 'passed' : check.blocking ? 'failed' : 'pending'}`}>
            <span aria-hidden="true">{check.passed ? '✓' : '✗'}</span>{' '}
            <span className="remediation-preflight-label">
              {check.label || CHECK_FALLBACK_LABELS[check.key] || check.key}
              <span className="visually-hidden">{check.passed ? ' (passed)' : ' (not passed)'}</span>
            </span>
            {!check.passed && !check.blocking && <span className="remediation-target-subtle"> (does not block saving)</span>}
            {check.detail && <span className="remediation-preflight-detail">{check.detail}</span>}
          </li>
        ))}
      </ul>

      {missing.length > 0 && (
        <div className="remediation-permission-explainer" role="note">
          <p><strong>KAIRON needs permission to:</strong></p>
          <ul className="remediation-permission-list">
            {required.map((right) => (
              <li key={right}>
                ✓ {right.toLowerCase()} {name}
                {missing.includes(right) && <span className="remediation-target-subtle"> (not granted yet)</span>}
              </li>
            ))}
          </ul>
          <p><strong>It will NOT receive:</strong></p>
          <ul className="remediation-permission-list remediation-permission-denied">
            <li>✗ Administrator access</li>
            <li>✗ Access to unrelated services</li>
            <li>✗ Permission to modify service configuration</li>
          </ul>
        </div>
      )}

      {result.fixCommand && (
        <div className="remediation-fix">
          <p><strong>How to fix</strong></p>
          <p className="sdk-hint">
            Run this once in an <strong>elevated PowerShell</strong> (Run as administrator), from the KAIRON repository or
            installation folder. It grants only the rights listed above, on only this service, to only the account KAIRON
            runs as{result.executorAccount ? <> (<code>{result.executorAccount}</code>)</> : null}. KAIRON never runs it for you.
          </p>
          <CodeBlock code={result.fixCommand} copyKey="fix-command" label="Copy fix command" />
        </div>
      )}
    </div>
  );
}

// ---- Guided create/edit wizard --------------------------------------------------------------

function TargetWizard({ initialForm, projects, machines, onCancel, onSaved }) {
  const isEditing = Boolean(initialForm.id);
  const [form, setForm] = useState({ kind: 'WindowsService', ...initialForm });
  const [step, setStep] = useState(0);
  const isApp = form.kind === 'AppProcess';
  const steps = wizardSteps(form.kind);
  const stepKey = steps[Math.min(step, steps.length - 1)].key;
  const [credentials, setCredentials] = useState([]);
  const [applications, setApplications] = useState([]);
  const [services, setServices] = useState({ loading: false, list: null, error: null });
  const [serviceSearch, setServiceSearch] = useState('');
  const [preflight, setPreflight] = useState({ loading: false, result: null, error: null, key: null });
  const [errors, setErrors] = useState([]);
  const [saving, setSaving] = useState(false);
  const preflightSeq = useRef(0);
  const inflightKey = useRef(null);
  const headingRef = useRef(null);

  const machineById = useMemo(() => new Map(machines.map((m) => [m.id, m])), [machines]);
  const activeCredentials = credentials.filter((c) => !c.revokedAt);

  // A preselected machine (or the single-machine default) may resolve before the machine list
  // has loaded; patch its host name in as soon as it is known.
  useEffect(() => {
    if (!form.machineId || form.expectedHostName) return;
    const machine = machineById.get(form.machineId);
    if (machine) setForm((prev) => ({ ...prev, expectedHostName: machine.hostName }));
  }, [machineById, form.machineId, form.expectedHostName]);

  // Credentials for the chosen project. Guarded against an out-of-order response when the operator
  // switches project quickly. Exactly one active credential is preselected - the common case.
  useEffect(() => {
    if (!form.projectId) { setCredentials([]); return undefined; }
    let stale = false;
    const projectId = form.projectId;
    sdkApi.listCredentials(projectId)
      .then((result) => {
        if (stale) return;
        const list = result || [];
        setCredentials(list);
        const active = list.filter((c) => !c.revokedAt);
        setForm((prev) => (prev.projectId === projectId && !prev.telemetryCredentialId && active.length === 1
          ? { ...prev, telemetryCredentialId: active[0].id }
          : prev));
      })
      .catch(() => { if (!stale) setCredentials([]); });
    Promise.resolve(sdkApi.listApplications?.(projectId))
      .then((apps) => { if (!stale) setApplications(apps || []); })
      .catch(() => { if (!stale) setApplications([]); });
    return () => { stale = true; };
  }, [form.projectId]);

  // Windows services on the chosen machine.
  const loadServices = (machineId) => {
    if (!machineId || !remediationTargetsApi.machineServices) {
      setServices({ loading: false, list: null, error: null });
      return undefined;
    }
    let stale = false;
    setServices({ loading: true, list: null, error: null });
    remediationTargetsApi.machineServices(machineId)
      .then((list) => { if (!stale) setServices({ loading: false, list: list || [], error: null }); })
      .catch((err) => { if (!stale) setServices({ loading: false, list: null, error: err || { message: 'Could not list services.' } }); });
    return () => { stale = true; };
  };

  // Only a Windows-service target needs the machine's service list.
  useEffect(() => (isApp ? undefined : loadServices(form.machineId)), [form.machineId, isApp]); // eslint-disable-line react-hooks/exhaustive-deps

  const payloadKey = isPayloadComplete(form)
    ? JSON.stringify(buildPayload(form, true))
    : null;

  // The permission check runs automatically - and only - once the request is complete, and again
  // whenever the service or operations change. A stale response never overwrites a newer one.
  const runPreflight = async () => {
    if (!payloadKey) return;
    const seq = ++preflightSeq.current;
    const key = payloadKey;
    inflightKey.current = key;
    setPreflight((prev) => ({ ...prev, loading: true, error: null }));
    try {
      const result = await remediationTargetsApi.preflight(buildPayload(form, true));
      if (seq === preflightSeq.current) setPreflight({ loading: false, result, error: null, key });
    } catch (err) {
      if (seq === preflightSeq.current) setPreflight({ loading: false, result: null, error: err, key });
    } finally {
      if (seq === preflightSeq.current) inflightKey.current = null;
    }
  };

  useEffect(() => {
    if ((stepKey === 'permission' || stepKey === 'review') && payloadKey && preflight.key !== payloadKey && inflightKey.current !== payloadKey) runPreflight();
  }, [step, payloadKey]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => { headingRef.current?.focus?.(); }, [step]);

  const update = (patch) => setForm((prev) => ({ ...prev, ...patch }));

  const stepValid = {
    app: Boolean(form.projectId && form.environment && form.service.trim() && form.telemetryCredentialId),
    machine: Boolean(form.machineId),
    service: Boolean(form.windowsServiceName.trim()),
    operations: form.allowedOperations.length > 0,
    permission: true,
    review: true
  };

  const chooseKind = (kind) => setForm((prev) => (prev.kind === kind ? prev
    : { ...prev, kind, windowsServiceName: '', allowedOperations: defaultOperations(kind) }));

  const save = async (enabled) => {
    if (!isPayloadComplete(form)) {
      setErrors(['Complete every step before saving.']);
      return;
    }
    setSaving(true);
    setErrors([]);
    try {
      if (isEditing) {
        await remediationTargetsApi.update(form.id, { ...buildPayload(form, enabled), expectedUpdatedAt: form.updatedAt });
      } else {
        await remediationTargetsApi.create(buildPayload(form, enabled));
      }
      onSaved(enabled
        ? (isEditing ? 'Remediation target updated and enabled.' : 'Remediation target enabled.')
        : (isEditing ? 'Remediation target saved (disabled).' : 'Remediation target saved (disabled).'));
    } catch (err) {
      setErrors(errorMessages(err, 'Could not save the remediation target.'));
    } finally {
      setSaving(false);
    }
  };

  const preflightCurrent = preflight.key === payloadKey ? preflight.result : null;
  const canEnable = Boolean(payloadKey && preflightCurrent?.canEnable && !preflight.loading);
  const machine = machineById.get(form.machineId);
  const credential = credentials.find((c) => c.id === form.telemetryCredentialId);
  const project = projects.find((p) => p.id === form.projectId);
  const selectedService = (services.list || []).find((s) => s.serviceName === form.windowsServiceName);
  const serviceSuggestions = [...new Set(applications.map((a) => a.service).filter(Boolean))].sort();

  return (
    <section className="section-card remediation-wizard" aria-labelledby="remediation-wizard-title">
      <div className="section-header">
        <div className="section-title-group">
          <div>
            <h3 id="remediation-wizard-title">{isEditing ? 'Edit remediation target' : 'Configure a remediation target'}</h3>
            <p className="section-desc">
              Step {step + 1} of {steps.length}: {steps[step].label}
            </p>
          </div>
        </div>
      </div>

      <StatusTimeline steps={steps} currentKey={stepKey} className="remediation-wizard-progress" />

      {errors.length > 0 && (
        <div className="remediation-target-form-errors" role="alert">
          <IconAlertTriangle className="w-4 h-4" />
          <ul>{errors.map((message, i) => <li key={i}>{message}</li>)}</ul>
        </div>
      )}

      <div className="remediation-wizard-body">
        <h4 tabIndex={-1} ref={headingRef} className="remediation-target-subheading">{steps[step].label}</h4>

        {stepKey === 'app' && !isEditing && (
          <fieldset className="remediation-target-operations remediation-choice-list">
            <legend>What should KAIRON restart when you approve a fix?</legend>
            {['AppProcess', 'WindowsService'].map((kind) => (
              <label key={kind} className="remediation-target-operation-checkbox">
                <input type="radio" name="remediation-kind" checked={form.kind === kind} onChange={() => chooseKind(kind)} />
                <span>
                  <strong>{kind === 'AppProcess' ? 'The application itself (recommended - no setup)' : 'A Windows service'}</strong>
                  <span className="remediation-preflight-detail">{TARGET_KINDS[kind].description}</span>
                </span>
              </label>
            ))}
          </fieldset>
        )}

        {stepKey === 'app' && (
          <div className="remediation-target-form">
            <label>
              Project
              <select
                className="approval-input"
                value={form.projectId}
                onChange={(e) => update({ projectId: e.target.value, telemetryCredentialId: '' })}
              >
                <option value="" disabled>Select a project...</option>
                {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </label>
            <label>
              Environment
              <select className="approval-input" value={form.environment} onChange={(e) => update({ environment: e.target.value })}>
                {ENVIRONMENTS.map((env) => <option key={env} value={env}>{env}</option>)}
              </select>
            </label>
            <label>
              Logical service
              <input
                className="approval-input"
                value={form.service}
                onChange={(e) => update({ service: e.target.value })}
                placeholder="The service name your application reports"
                list="remediation-service-suggestions"
                autoComplete="off"
              />
              <datalist id="remediation-service-suggestions">
                {serviceSuggestions.map((name) => <option key={name} value={name} />)}
              </datalist>
            </label>
            <label>
              App credential
              <select
                className="approval-input"
                disabled={!form.projectId}
                value={form.telemetryCredentialId}
                onChange={(e) => update({ telemetryCredentialId: e.target.value })}
              >
                <option value="" disabled>{form.projectId ? 'Select a credential...' : 'Select a project first'}</option>
                {activeCredentials.map((c) => <option key={c.id} value={c.id}>{c.name} ({c.keyPrefix}...)</option>)}
              </select>
            </label>
            {serviceSuggestions.length > 0 && (
              <p className="remediation-target-derived-field">
                Services seen from this project: {serviceSuggestions.join(', ')}
              </p>
            )}
            {form.projectId && activeCredentials.length === 0 && (
              <p className="remediation-target-derived-field">
                This project has no active app credential yet. Connect the application first (Connect an app).
              </p>
            )}
          </div>
        )}

        {stepKey === 'machine' && (
          <fieldset className="remediation-target-operations remediation-choice-list">
            <legend>Machine</legend>
            {machines.length === 0 && <p className="panel-pending-text">No enrolled machines. Install and run the KAIRON Agent first.</p>}
            {machines.map((m) => (
              <label key={m.id} className="remediation-target-operation-checkbox">
                <input
                  type="radio"
                  name="remediation-machine"
                  checked={form.machineId === m.id}
                  onChange={() => update({ machineId: m.id, expectedHostName: m.hostName, windowsServiceName: form.machineId === m.id ? form.windowsServiceName : '' })}
                />
                <span>
                  <strong>{m.hostName}</strong>{' '}
                  <span className="remediation-target-subtle">{m.operatingSystem}{m.status ? ` · ${m.status}` : ''}</span>
                </span>
              </label>
            ))}
            <p className="remediation-target-derived-field">
              {isApp
                ? 'The machine the application runs on. Only applications on the machine running KAIRON can be restarted.'
                : 'Only services on the machine running KAIRON itself can be remediated.'}
            </p>
          </fieldset>
        )}

        {stepKey === 'service' && (
          <ServicePicker
            services={services}
            search={serviceSearch}
            onSearch={setServiceSearch}
            value={form.windowsServiceName}
            onChange={(name) => update({ windowsServiceName: name })}
            onRetry={() => loadServices(form.machineId)}
          />
        )}

        {stepKey === 'operations' && isApp && (
          <fieldset className="remediation-target-operations remediation-choice-list">
            <legend>Allowed operations</legend>
            {APP_OPERATIONS.map((op) => (
              <label key={op.id} className="remediation-target-operation-checkbox">
                <input type="checkbox" checked readOnly disabled />
                <span>
                  <strong>{op.label}</strong> <RiskBadge risk={op.risk} />
                  <span className="remediation-preflight-detail">{op.description}</span>
                </span>
              </label>
            ))}
            <p className="remediation-target-derived-field">
              The KAIRON UserAgent restarts the app as the same Windows user, with the same program, arguments and folder.
              It only ever happens after you approve it on an incident.
            </p>
          </fieldset>
        )}

        {stepKey === 'operations' && !isApp && (
          <fieldset className="remediation-target-operations remediation-choice-list">
            <legend>Allowed operations</legend>
            {OPERATIONS.map((op) => (
              <label key={op.id} className="remediation-target-operation-checkbox">
                <input
                  type="checkbox"
                  checked={form.allowedOperations.includes(op.id)}
                  onChange={() => update({
                    allowedOperations: form.allowedOperations.includes(op.id)
                      ? form.allowedOperations.filter((o) => o !== op.id)
                      : [...form.allowedOperations, op.id]
                  })}
                />
                <span>
                  <strong>{op.label}</strong> <RiskBadge risk={op.risk} />
                  <span className="remediation-preflight-detail">{op.description}</span>
                </span>
              </label>
            ))}
            <p className="remediation-target-derived-field">Only allow what you need. A health check changes nothing.</p>
          </fieldset>
        )}

        {stepKey === 'permission' && (
          <div>
            {!payloadKey ? (
              <p className="panel-pending-text">Complete the previous steps to run the {isApp ? 'readiness' : 'permission'} check.</p>
            ) : preflight.loading && !preflightCurrent ? (
              <p className="panel-pending-text">{isApp ? 'Checking the application...' : 'Checking Windows permissions...'}</p>
            ) : preflight.error && preflight.key === payloadKey ? (
              <p className="panel-pending-text">{errorMessages(preflight.error, 'Could not run the permission check.').join(' ')}</p>
            ) : (
              <>
                {preflightCurrent && (
                  <p className={preflightCurrent.canEnable ? 'remediation-preflight-ok' : 'remediation-preflight-blocked'} role="status">
                    {preflightCurrent.canEnable
                      ? '✓ All required checks pass. This target can be enabled.'
                      : `✗ This target cannot be enabled yet: ${describeReadiness(preflightCurrent.readiness).label}.`}
                  </p>
                )}
                <PreflightResult result={preflightCurrent} serviceName={form.windowsServiceName} />
              </>
            )}
            <Button size="compact" variant="secondary" disabled={!payloadKey || preflight.loading} onClick={runPreflight}>
              {preflight.loading ? 'Checking...' : 'Re-check'}
            </Button>
          </div>
        )}

        {stepKey === 'review' && (
          <div>
            <dl className="remediation-target-summary">
              <div><dt>Project</dt><dd>{project?.name || 'Not selected'}</dd></div>
              <div><dt>Environment</dt><dd>{form.environment}</dd></div>
              <div><dt>Logical service</dt><dd>{form.service || 'Not set'}</dd></div>
              <div><dt>App credential</dt><dd>{credential?.name || 'Not selected'}</dd></div>
              <div><dt>Machine</dt><dd>{machine?.hostName || form.expectedHostName || 'Not selected'}</dd></div>
              {isApp
                ? <div><dt>Restarts</dt><dd>The application itself</dd></div>
                : <div><dt>Windows service</dt><dd>{selectedService?.displayName ? `${selectedService.displayName} (${form.windowsServiceName})` : form.windowsServiceName || 'Not selected'}</dd></div>}
              <div><dt>Operations</dt><dd>{form.allowedOperations.map(operationLabel).join(', ') || 'None'}</dd></div>
              <div>
                <dt>{isApp ? 'Readiness check' : 'Permission check'}</dt>
                <dd>
                  {!preflightCurrent
                    ? (preflight.loading ? 'Checking...' : 'Not run yet')
                    : preflightCurrent.canEnable ? '✓ Passed' : `✗ ${describeReadiness(preflightCurrent.readiness).label}`}
                </dd>
              </div>
            </dl>
            {!canEnable && (
              <p className="remediation-target-derived-field">
                Enable becomes available once the {isApp ? 'readiness' : 'permission'} check passes. You can save the target disabled now and enable it later.
              </p>
            )}
          </div>
        )}
      </div>

      <div className="remediation-target-form-actions">
        {step > 0 && (
          <Button variant="secondary" onClick={() => setStep((s) => s - 1)} disabled={saving}>Back</Button>
        )}
        {step < steps.length - 1 && (
          <Button variant="primary" onClick={() => setStep((s) => s + 1)} disabled={!stepValid[stepKey]}>Next</Button>
        )}
        {step === steps.length - 1 && (
          <>
            <Button variant="primary" onClick={() => save(true)} disabled={saving || !canEnable}>
              {saving ? 'Saving...' : isEditing ? 'Save and enable' : 'Enable'}
            </Button>
            <Button variant="secondary" onClick={() => save(false)} disabled={saving || !payloadKey}>
              Save disabled
            </Button>
          </>
        )}
        <Button variant="ghost" onClick={onCancel} disabled={saving}>Cancel</Button>
      </div>
    </section>
  );
}

/** Pick a Windows service from the machine's real list: eligible first, ineligible shown but
 * disabled with the reason. Typing a name is only a fallback under "Advanced". */
function ServicePicker({ services, search, onSearch, value, onChange, onRetry }) {
  const query = search.trim().toLowerCase();
  const matches = (s) => !query || s.serviceName.toLowerCase().includes(query) || (s.displayName || '').toLowerCase().includes(query);
  const list = (services.list || []).filter(matches);
  const eligible = list.filter((s) => s.eligible).sort((a, b) => (a.displayName || a.serviceName).localeCompare(b.displayName || b.serviceName));
  const ineligible = list.filter((s) => !s.eligible);
  const remote = services.error?.code === 'remote-not-supported';
  const inList = (services.list || []).some((s) => s.serviceName === value);

  const option = (s) => (
    <label key={s.serviceName} className={`remediation-target-operation-checkbox ${s.eligible ? '' : 'remediation-choice-disabled'}`}>
      <input
        type="radio"
        name="remediation-windows-service"
        disabled={!s.eligible}
        checked={value === s.serviceName}
        onChange={() => onChange(s.serviceName)}
      />
      <span>
        <strong>{s.displayName || s.serviceName}</strong>{' '}
        <span className="remediation-target-subtle">{s.serviceName}{s.state ? ` · ${s.state}` : ''}</span>
        {!s.eligible && (
          <span className="remediation-preflight-detail">Not available: {s.eligibilityDetail || s.eligibility}</span>
        )}
      </span>
    </label>
  );

  return (
    <div>
      {services.loading && <p className="panel-pending-text">Loading Windows services...</p>}
      {services.error && (
        <div className="remediation-target-form-errors" role="alert">
          <IconAlertTriangle className="w-4 h-4" />
          <ul>
            <li>
              {remote
                ? 'This machine is not the one running KAIRON. Only services on the KAIRON machine can be remediated - choose that machine in the previous step.'
                : services.error.message || 'Could not list the services on this machine.'}
            </li>
          </ul>
          {!remote && <Button size="compact" variant="secondary" onClick={onRetry}>Retry</Button>}
        </div>
      )}

      {services.list && (
        <>
          <label className="remediation-service-search">
            Search services
            <input
              className="approval-input"
              type="search"
              value={search}
              onChange={(e) => onSearch(e.target.value)}
              placeholder="Name or display name"
              autoComplete="off"
            />
          </label>
          <fieldset className="remediation-target-operations remediation-choice-list remediation-service-list">
            <legend>Services KAIRON can manage ({eligible.length})</legend>
            {eligible.length === 0 && <p className="panel-pending-text">No matching eligible services.</p>}
            {eligible.map(option)}
          </fieldset>
          {ineligible.length > 0 && (
            <details className="sdk-guide-details">
              <summary>Show {ineligible.length} service{ineligible.length === 1 ? '' : 's'} KAIRON cannot manage</summary>
              <fieldset className="remediation-target-operations remediation-choice-list remediation-service-list">
                <legend>Not available</legend>
                {ineligible.map(option)}
              </fieldset>
            </details>
          )}
        </>
      )}

      <details className="sdk-guide-details" open={Boolean(value && services.list && !inList) || undefined}>
        <summary>Advanced: enter a service name manually</summary>
        <label className="remediation-service-search">
          Windows service name
          <input
            className="approval-input"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            placeholder="The exact Windows service name"
            autoComplete="off"
          />
        </label>
        <p className="sdk-hint">The permission check still verifies the service exists and is eligible before it can be enabled.</p>
      </details>
    </div>
  );
}
