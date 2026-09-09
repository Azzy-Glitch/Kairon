import React, { useEffect, useMemo, useState } from 'react';
import { remediationTargetsApi, sdkApi, agentApi } from '../../api';
import { useToast } from '../Toast';
import DataTable from '../ui/DataTable';
import EmptyState from '../ui/EmptyState';
import Button from '../ui/Button';
import Badge from '../ui/Badge';
import { IconZap, IconAlertTriangle } from '../Icons';

const ENVIRONMENTS = ['Development', 'Staging', 'Production'];
// Must correspond exactly to the backend's supported operations
// (backend/Services/Remediation/Tools/WindowsServiceTools.cs - ServiceToolNames). Never add to
// this list without a matching backend change; the API rejects anything else as unknown.
const OPERATIONS = ['RestartService', 'StartService', 'StopService', 'RunHealthCheck'];

function emptyForm() {
  return {
    id: null,
    projectId: '',
    environment: 'Production',
    service: '',
    machineId: '',
    expectedHostName: '',
    telemetryCredentialId: '',
    windowsServiceName: '',
    allowedOperations: [],
    enabled: true,
    updatedAt: null
  };
}

/**
 * Operator-facing management UI for the database-backed remediation-target system
 * (backend/Controllers/RemediationTargetsController.cs). Configuration only: creating, editing,
 * disabling and enabling a target here never executes anything, approves anything, or bypasses
 * the runtime resolver's own revalidation at execution time - it only ever changes what is
 * persisted (see that controller's remarks).
 *
 * preselectMachineId: set by MachinesPage's "Configure remediation target" shortcut - opens the
 * create form with that machine already selected. A UX shortcut only; there is exactly one
 * remediation-target configuration system, this page.
 */
export default function RemediationTargetsPage({ preselectMachineId }) {
  const toast = useToast();
  const [targets, setTargets] = useState(null);
  const [loadError, setLoadError] = useState(null);
  const [projects, setProjects] = useState([]);
  const [machines, setMachines] = useState([]);
  const [credentials, setCredentials] = useState([]);
  const [form, setForm] = useState(null); // null = hidden; object = create/edit form open
  const [formErrors, setFormErrors] = useState([]);
  const [saving, setSaving] = useState(false);
  const [checking, setChecking] = useState(false);
  const [confirmDisableId, setConfirmDisableId] = useState(null);
  const [busyId, setBusyId] = useState(null);

  const machineById = useMemo(() => new Map(machines.map((m) => [m.id, m])), [machines]);

  const load = async () => {
    try {
      const list = await remediationTargetsApi.list();
      setTargets(list);
      setLoadError(null);
    } catch (err) {
      setLoadError(err);
    }
  };

  useEffect(() => {
    load();
    sdkApi.listProjects().then(setProjects).catch(() => {});
    agentApi.getMachines().then(setMachines).catch(() => {});
  }, []);

  useEffect(() => {
    if (preselectMachineId) {
      setForm({ ...emptyForm(), machineId: preselectMachineId });
      setFormErrors([]);
    }
    // Only ever react to the shortcut actually changing which machine it points at.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preselectMachineId]);

  // The machine list (agentApi.getMachines) loads asynchronously and can still be empty at the
  // moment the effect above runs, which would otherwise leave expectedHostName blank forever for
  // a preselected machine. Patches it in as soon as the machine becomes known, without disturbing
  // anything else the operator may have already typed into the form.
  useEffect(() => {
    if (!form?.machineId || form.expectedHostName) return;
    const machine = machineById.get(form.machineId);
    if (machine) setForm((prev) => (prev ? { ...prev, expectedHostName: machine.hostName } : prev));
  }, [machineById, form?.machineId, form?.expectedHostName]);

  useEffect(() => {
    if (!form?.projectId) {
      setCredentials([]);
      return;
    }
    sdkApi.listCredentials(form.projectId).then(setCredentials).catch(() => setCredentials([]));
  }, [form?.projectId]);

  const openCreate = () => {
    setForm(emptyForm());
    setFormErrors([]);
  };

  const openEdit = (target) => {
    setForm({
      id: target.id,
      projectId: target.projectId,
      environment: target.environment,
      service: target.service,
      machineId: target.machineId,
      expectedHostName: target.expectedHostName,
      telemetryCredentialId: target.telemetryCredentialId,
      windowsServiceName: target.windowsServiceName,
      allowedOperations: target.allowedOperations,
      enabled: target.enabled,
      updatedAt: target.updatedAt
    });
    setFormErrors([]);
  };

  const closeForm = () => {
    setForm(null);
    setFormErrors([]);
  };

  const selectMachine = (machineId) => {
    const machine = machineById.get(machineId);
    setForm((prev) => ({ ...prev, machineId, expectedHostName: machine?.hostName || '' }));
  };

  const toggleOperation = (op) => {
    setForm((prev) => ({
      ...prev,
      allowedOperations: prev.allowedOperations.includes(op)
        ? prev.allowedOperations.filter((o) => o !== op)
        : [...prev.allowedOperations, op]
    }));
  };

  const buildPayload = () => ({
    projectId: form.projectId,
    environment: form.environment,
    service: form.service.trim(),
    machineId: form.machineId,
    telemetryCredentialId: form.telemetryCredentialId,
    expectedHostName: form.expectedHostName,
    windowsServiceName: form.windowsServiceName.trim(),
    allowedOperations: form.allowedOperations,
    enabled: form.enabled
  });

  const isFormComplete = form && form.projectId && form.service.trim() && form.machineId &&
    form.telemetryCredentialId && form.windowsServiceName.trim() && form.allowedOperations.length > 0;

  const handleCheck = async () => {
    setChecking(true);
    setFormErrors([]);
    try {
      const result = await remediationTargetsApi.validate(buildPayload());
      if (result.valid) toast.addToast('No validation errors.', 'success');
      else setFormErrors(result.errors);
    } catch (err) {
      toast.addToast(err?.message || 'Could not run validation', 'error');
    } finally {
      setChecking(false);
    }
  };

  const handleSave = async (e) => {
    e.preventDefault();
    setSaving(true);
    setFormErrors([]);
    try {
      if (form.id) {
        await remediationTargetsApi.update(form.id, { ...buildPayload(), expectedUpdatedAt: form.updatedAt });
      } else {
        await remediationTargetsApi.create(buildPayload());
      }
      toast.addToast(form.id ? 'Remediation target updated.' : 'Remediation target created.', 'success');
      closeForm();
      load();
    } catch (err) {
      if (err.code === 'stale-update') {
        setFormErrors(['This target was changed by another operator. Reload the latest version before editing it again.']);
      } else if (err.status === 409 || err.status === 422 || err.status === 400) {
        setFormErrors([err.message]);
      } else {
        toast.addToast(err?.message || 'Could not save the remediation target.', 'error');
      }
    } finally {
      setSaving(false);
    }
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
      // Enabling re-validates in full server-side (project/machine/credential/uniqueness) - a
      // stale disabled target can legitimately fail here even though it saved fine while disabled.
      toast.addToast(err?.message || 'Could not enable the target - it may no longer be valid.', 'error');
    } finally {
      setBusyId(null);
    }
  };

  const columns = [
    { key: 'projectName', label: 'Project', sortable: true, render: (t) => t.projectName || t.projectId },
    { key: 'environment', label: 'Environment', sortable: true },
    { key: 'service', label: 'Service', sortable: true },
    { key: 'machineHostName', label: 'Machine', priority: 1, render: (t) => t.machineHostName || t.machineId },
    { key: 'windowsServiceName', label: 'Windows Service', priority: 1 },
    {
      key: 'allowedOperations',
      label: 'Allowed Operations',
      priority: 2,
      render: (t) => t.allowedOperations.join(', ')
    },
    {
      key: 'enabled',
      label: 'Status',
      render: (t) => <Badge tone={t.enabled ? 'healthy' : 'neutral'}>{t.enabled ? 'Enabled' : 'Disabled'}</Badge>
    },
    {
      key: 'updatedAt',
      label: 'Last Updated',
      priority: 1,
      sortable: true,
      render: (t) => new Date(t.updatedAt).toLocaleString()
    },
    {
      key: 'actions',
      label: '',
      render: (t) => (
        <span className="remediation-target-actions">
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
                The exact machine, Windows service and allowed operations KAIRON is authorized to remediate for a
                project/environment/service. Configuration only - approval and execution are unchanged.
              </p>
            </div>
          </div>
          {!form && <Button variant="primary" onClick={openCreate}>+ New Target</Button>}
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
            description="Create one to let KAIRON restart, start, stop or health-check a specific Windows service on a specific machine."
            action={<Button variant="primary" onClick={openCreate}>+ New Target</Button>}
          />
        ) : (
          <DataTable columns={columns} rows={targets} getRowKey={(t) => t.id} sortableDefaultKey="updatedAt" />
        )}
      </section>

      {form && (
        <TargetForm
          form={form}
          setForm={setForm}
          projects={projects}
          machines={machines}
          credentials={credentials}
          errors={formErrors}
          saving={saving}
          checking={checking}
          isFormComplete={isFormComplete}
          onSelectMachine={selectMachine}
          onToggleOperation={toggleOperation}
          onCheck={handleCheck}
          onSave={handleSave}
          onCancel={closeForm}
        />
      )}
    </div>
  );
}

function TargetForm({
  form, setForm, projects, machines, credentials, errors, saving, checking, isFormComplete,
  onSelectMachine, onToggleOperation, onCheck, onSave, onCancel
}) {
  const isEditing = Boolean(form.id);
  const activeCredentials = credentials.filter((c) => !c.revokedAt);

  return (
    <section className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div>
            <h3>{isEditing ? 'Edit remediation target' : 'New remediation target'}</h3>
            <p className="section-desc">
              {isEditing
                ? 'Every security-sensitive field is revalidated on save, exactly as if this were a new target.'
                : 'Project, machine and telemetry credential are verified against the database - not just accepted as typed.'}
            </p>
          </div>
        </div>
      </div>

      {errors.length > 0 && (
        <div className="remediation-target-form-errors">
          <IconAlertTriangle className="w-4 h-4" />
          <ul>
            {errors.map((message, i) => <li key={i}>{message}</li>)}
          </ul>
        </div>
      )}

      <form className="remediation-target-form" onSubmit={onSave}>
        <label>
          Project
          <select
            className="approval-input"
            required
            value={form.projectId}
            onChange={(e) => setForm((prev) => ({ ...prev, projectId: e.target.value, telemetryCredentialId: '' }))}
          >
            <option value="" disabled>Select a project...</option>
            {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
        </label>

        <label>
          Environment
          <select
            className="approval-input"
            value={form.environment}
            onChange={(e) => setForm((prev) => ({ ...prev, environment: e.target.value }))}
          >
            {ENVIRONMENTS.map((env) => <option key={env} value={env}>{env}</option>)}
          </select>
        </label>

        <label>
          Logical Service
          <input
            className="approval-input"
            required
            value={form.service}
            onChange={(e) => setForm((prev) => ({ ...prev, service: e.target.value }))}
            placeholder="e.g. OrderProcessingService"
            autoComplete="off"
          />
        </label>

        <label>
          Machine
          <select
            className="approval-input"
            required
            value={form.machineId}
            onChange={(e) => onSelectMachine(e.target.value)}
          >
            <option value="" disabled>Select a machine...</option>
            {machines.map((m) => <option key={m.id} value={m.id}>{m.hostName} ({m.operatingSystem})</option>)}
          </select>
        </label>

        {form.machineId && (
          <p className="remediation-target-derived-field">
            Expected host name: <code className="path-code">{form.expectedHostName || '(unknown machine)'}</code>
          </p>
        )}

        <label>
          Windows Service Name
          <input
            className="approval-input"
            required
            value={form.windowsServiceName}
            onChange={(e) => setForm((prev) => ({ ...prev, windowsServiceName: e.target.value }))}
            placeholder="The exact Windows SCM service name"
            autoComplete="off"
          />
        </label>

        <label>
          Telemetry Credential
          <select
            className="approval-input"
            required
            disabled={!form.projectId}
            value={form.telemetryCredentialId}
            onChange={(e) => setForm((prev) => ({ ...prev, telemetryCredentialId: e.target.value }))}
          >
            <option value="" disabled>
              {form.projectId ? 'Select a credential...' : 'Select a project first'}
            </option>
            {activeCredentials.map((c) => (
              <option key={c.id} value={c.id}>{c.name} ({c.keyPrefix}...)</option>
            ))}
          </select>
        </label>

        <fieldset className="remediation-target-operations">
          <legend>Allowed Operations</legend>
          {OPERATIONS.map((op) => (
            <label key={op} className="remediation-target-operation-checkbox">
              <input
                type="checkbox"
                checked={form.allowedOperations.includes(op)}
                onChange={() => onToggleOperation(op)}
              />
              {op}
            </label>
          ))}
        </fieldset>

        {!isEditing && (
          <label className="remediation-target-operation-checkbox">
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(e) => setForm((prev) => ({ ...prev, enabled: e.target.checked }))}
            />
            Enabled immediately (uncheck to stage this target without full validation, then enable it later)
          </label>
        )}

        <div className="remediation-target-form-actions">
          <Button type="button" variant="secondary" disabled={checking} onClick={onCheck}>
            {checking ? 'Checking...' : 'Check for errors'}
          </Button>
          <Button type="submit" variant="primary" disabled={saving || !isFormComplete}>
            {saving ? 'Saving...' : isEditing ? 'Save changes' : 'Create target'}
          </Button>
          <Button type="button" variant="ghost" onClick={onCancel}>Cancel</Button>
        </div>
      </form>
    </section>
  );
}
