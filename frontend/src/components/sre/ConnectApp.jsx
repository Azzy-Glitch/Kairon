import React, { useEffect, useMemo, useState } from 'react';
import { sdkApi } from '../../api';
import { useToast } from '../Toast';
import { IconCheck, IconAlertTriangle, IconLink } from '../Icons';

/**
 * The first screen of "Connect an app": four steps, nothing else.
 *
 *   1 Select framework -> 2 Generate pairing code -> 3 Copy minimal code -> 4 Detect connection
 *
 * The framework is the single source of truth for the SDK type: the pairing code is always
 * generated for the selected framework's SDK, and the code shown in step 3 is always that
 * framework's. Connection detection is driven by the pairing session itself (redeemed ->
 * confirmed -> machine -> telemetry), never by "some telemetry exists in this project".
 */

export const FRAMEWORKS = [
  { id: 'fastapi', label: 'FastAPI', language: 'Python', sdkType: 'python' },
  { id: 'flask', label: 'Flask', language: 'Python', sdkType: 'python' },
  { id: 'django', label: 'Django', language: 'Python', sdkType: 'python' },
  { id: 'aspnetcore', label: 'ASP.NET Core', language: '.NET', sdkType: 'dotnet' },
  { id: 'dotnet-standalone', label: 'Standalone .NET', language: '.NET', sdkType: 'dotnet' }
];

export const DEFAULT_FRAMEWORK_ID = 'fastapi';

export function frameworkById(id) {
  return FRAMEWORKS.find((f) => f.id === id) || FRAMEWORKS[0];
}

export const ENVIRONMENTS = ['Development', 'Staging', 'Production'];

/** Gap between pairing-status checks while waiting for the application. */
export const CONNECTION_POLL_INTERVAL_MS = 3000;
/** After this long without a full connection, checks slow down to CONNECTION_SLOW_POLL_MS. */
export const CONNECTION_SLOW_AFTER_MS = 2 * 60 * 1000;
export const CONNECTION_SLOW_POLL_MS = 10000;
/** Upper bound on transient-failure backoff. */
export const CONNECTION_MAX_BACKOFF_MS = 60000;
/** Never watch forever: after this the operator can choose to check again. */
export const CONNECTION_WATCH_LIMIT_MS = 30 * 60 * 1000;

const PLACEHOLDER_CODE = 'YOUR_PAIRING_CODE';

/** Install command per framework. Only real package names (sdk-python/pyproject.toml "kairon-sdk",
 * sdk/Kairon.SDK PackageId "Kairon.SDK"). */
export function installCommand(framework) {
  return framework.sdkType === 'python' ? 'pip install kairon-sdk' : 'dotnet add package Kairon.SDK';
}

/** The minimal code for a framework, with the real pairing code inserted. Every API used here is
 * the one the shipped SDK exposes (Kairon.attach, kairon.django.KaironMiddleware,
 * AddKaironAsync/UseKairon, KaironClient). */
export function minimalCode(framework, code) {
  const c = code || PLACEHOLDER_CODE;
  switch (framework.id) {
    case 'flask':
      return `from flask import Flask
from kairon import Kairon

app = Flask(__name__)

Kairon.attach(app, pairing_code="${c}")`;
    case 'django':
      return `# settings.py
MIDDLEWARE = ["kairon.django.KaironMiddleware", *MIDDLEWARE]

KAIRON = {"pairing_code": "${c}"}`;
    case 'aspnetcore':
      return `using Kairon.SDK;

var builder = WebApplication.CreateBuilder(args);

await builder.Services.AddKaironAsync("${c}");

var app = builder.Build();
app.UseKairon();

app.Run();`;
    case 'dotnet-standalone':
      return `using Kairon.SDK;

var kairon = new KaironClient(pairingCode: "${c}");
kairon.Start();`;
    case 'fastapi':
    default:
      return `from fastapi import FastAPI
from kairon import Kairon

app = FastAPI()

Kairon.attach(app, pairing_code="${c}")`;
  }
}

/** What to change after the first successful run, so the single-use code isn't left in source. */
function afterFirstRunHint(framework) {
  switch (framework.id) {
    case 'flask':
    case 'fastapi':
      return <>After the first successful run, change that line to <code>Kairon.attach(app)</code> - the connection is stored securely and reused.</>;
    case 'django':
      return <>After the first successful run, remove <code>pairing_code</code> from <code>KAIRON</code> - the connection is stored securely and reused.</>;
    case 'aspnetcore':
      return <>After the first successful run, change that line to <code>builder.Services.AddKairon();</code> - the connection is stored securely and reused.</>;
    default:
      return <>After the first successful run, use <code>new KaironClient()</code> - the connection is stored securely and reused.</>;
  }
}

export default function ConnectApp({
  CodeBlock, framework, onFrameworkChange, projects, selectedProjectId, onSelectProject, onCreateProject,
  creatingProject, onTelemetry
}) {
  const toast = useToast();
  const [environment, setEnvironment] = useState('Development');
  const [service, setService] = useState('');
  const [newProjectName, setNewProjectName] = useState('');
  const [suggestions, setSuggestions] = useState([]);
  const [generating, setGenerating] = useState(false);
  const [pairing, setPairing] = useState(null); // { pairingId, code, expiresAt, sdkType, projectId, environment, service, frameworkLabel }

  useEffect(() => {
    if (!selectedProjectId) { setSuggestions([]); return undefined; }
    let stale = false;
    Promise.resolve(sdkApi.listApplications?.(selectedProjectId))
      .then((apps) => {
        if (stale) return;
        const names = [...new Set((apps || []).map((a) => a.service).filter(Boolean))].sort();
        setSuggestions(names);
      })
      .catch(() => { if (!stale) setSuggestions([]); });
    return () => { stale = true; };
  }, [selectedProjectId]);

  const handleCreateProject = async (e) => {
    e.preventDefault();
    const name = newProjectName.trim();
    if (!name) return;
    if (await onCreateProject(name)) setNewProjectName('');
  };

  const handleGenerate = async () => {
    if (!selectedProjectId) return;
    setGenerating(true);
    try {
      const result = await sdkApi.createPairing(selectedProjectId, framework.sdkType, undefined, { environment, service });
      setPairing({
        pairingId: result.pairingId,
        code: result.code,
        expiresAt: result.expiresAt,
        sdkType: result.sdkType || framework.sdkType,
        projectId: selectedProjectId,
        environment: result.environment || environment,
        service: result.service || service.trim() || null,
        frameworkLabel: framework.label
      });
    } catch (err) {
      toast.addToast(err?.message || 'Could not generate a pairing code', 'error');
    } finally {
      setGenerating(false);
    }
  };

  // A code is only ever shown in the snippet of an SDK that can actually redeem it.
  const codeMatchesFramework = pairing && pairing.sdkType === framework.sdkType;
  const snippetCode = codeMatchesFramework ? pairing.code : null;
  const projectName = (projects || []).find((p) => p.id === pairing?.projectId)?.name;

  const pythonFrameworks = FRAMEWORKS.filter((f) => f.sdkType === 'python');
  const dotnetFrameworks = FRAMEWORKS.filter((f) => f.sdkType === 'dotnet');

  return (
    <div className="sdk-get-started sdk-onboarding sdk-connect-flow">
      <header className="sdk-onboarding-intro">
        <h3>How do I connect my application to KAIRON?</h3>
        <p>Four steps. You only need a pairing code - KAIRON sets up the project, credential and endpoint for you.</p>
      </header>

      <Step number="1" title="Select your framework">
        <div className="sdk-framework-picker" role="radiogroup" aria-label="Framework">
          {[['Python', pythonFrameworks], ['.NET', dotnetFrameworks]].map(([language, list]) => (
            <div key={language} className="sdk-framework-group">
              <span className="sdk-framework-group-label">{language}</span>
              <div className="sdk-framework-options">
                {list.map((f) => (
                  <label key={f.id} className={`sdk-framework-option ${framework.id === f.id ? 'active' : ''}`}>
                    <input
                      type="radio"
                      name="sdk-framework"
                      value={f.id}
                      checked={framework.id === f.id}
                      onChange={() => onFrameworkChange(f.id)}
                    />
                    {f.label}
                  </label>
                ))}
              </div>
            </div>
          ))}
        </div>
      </Step>

      <Step number="2" title="Generate a pairing code">
        <div className="sdk-connect-form">
          <label>
            Project
            {projects === null ? (
              <span className="panel-pending-text">Loading projects...</span>
            ) : projects.length === 0 ? (
              <span className="sdk-hint">No projects yet - create one below.</span>
            ) : (
              <select
                className="approval-input"
                value={selectedProjectId || ''}
                onChange={(e) => onSelectProject(e.target.value)}
              >
                {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            )}
          </label>

          <label>
            Environment
            <select className="approval-input" value={environment} onChange={(e) => setEnvironment(e.target.value)}>
              {ENVIRONMENTS.map((env) => <option key={env} value={env}>{env}</option>)}
            </select>
          </label>

          <label>
            Service name (optional)
            <input
              className="approval-input"
              value={service}
              onChange={(e) => setService(e.target.value)}
              placeholder="e.g. OrdersService"
              list="sdk-service-suggestions"
              autoComplete="off"
            />
            <datalist id="sdk-service-suggestions">
              {suggestions.map((name) => <option key={name} value={name} />)}
            </datalist>
          </label>
        </div>

        <form className="sdk-new-project-form" onSubmit={handleCreateProject}>
          <input
            className="approval-input"
            value={newProjectName}
            onChange={(e) => setNewProjectName(e.target.value)}
            placeholder="New project name"
            aria-label="New project name"
            autoComplete="off"
          />
          <button type="submit" className="small-btn" disabled={creatingProject || !newProjectName.trim()}>
            {creatingProject ? 'Creating...' : '+ New Project'}
          </button>
        </form>

        <div className="sdk-pairing-form">
          <button
            type="button"
            className="small-btn sdk-primary-action"
            onClick={handleGenerate}
            disabled={generating || !selectedProjectId}
          >
            {generating ? 'Generating...' : `Generate ${framework.language} pairing code`}
          </button>
        </div>

        {pairing && (
          <div className="sdk-pairing-result">
            <p className="sdk-step-label">
              Pairing code for {pairing.frameworkLabel} (single use, expires {new Date(pairing.expiresAt).toLocaleTimeString()})
            </p>
            <CodeBlock code={pairing.code} copyKey="pairing-code" />
            <p className="sdk-hint">
              {projectName ? <>Project <strong>{projectName}</strong> · </> : null}
              Environment <strong>{pairing.environment}</strong>
              {pairing.service ? <> · Service <strong>{pairing.service}</strong></> : null}
            </p>
            {!codeMatchesFramework && (
              <p className="sdk-hint sdk-hint-warning" role="alert">
                <IconAlertTriangle className="w-3.5 h-3.5" /> This code was generated for {pairing.sdkType === 'python' ? 'Python' : '.NET'} and
                can't be redeemed by the {framework.language} SDK. Generate a new code for {framework.label}.
              </p>
            )}
          </div>
        )}
      </Step>

      <Step number="3" title="Add KAIRON to your application">
        <p>Install the SDK:</p>
        <CodeBlock code={installCommand(framework)} copyKey={`install-${framework.id}`} />
        <p className="sdk-hint">
          {framework.sdkType === 'python'
            ? <>From a KAIRON source checkout instead: <code>python -m pip install ./sdk-python</code>.</>
            : <>Use the package feed or local <code>.nupkg</code> folder your KAIRON release owner supplies (<code>--source &lt;feed&gt;</code>).</>}
        </p>
        <p>Then add {framework.id === 'aspnetcore' ? 'these lines to Program.cs' : 'this code'}:</p>
        <CodeBlock code={minimalCode(framework, snippetCode)} copyKey={`connect-${framework.id}`} />
        {!snippetCode && <p className="sdk-hint">Generate a pairing code in step 2 and it is inserted here automatically.</p>}
        <p className="sdk-hint">{afterFirstRunHint(framework)}</p>
        <p className="sdk-hint">
          KAIRON on this machine needs nothing else. For a KAIRON backend on another machine, set <code>KAIRON_ENDPOINT</code> to
          its <strong>HTTPS</strong> address (for example <code>https://kairon.example.com</code>) before the first run - the
          SDK only accepts plain HTTP for a loopback address such as <code>http://127.0.0.1:8000</code>.
        </p>
      </Step>

      <Step number="4" title="Run your application">
        <ConnectionStatus pairing={pairing} onTelemetry={onTelemetry} />
      </Step>
    </div>
  );
}

function Step({ number, title, children }) {
  return (
    <section className="section-card sdk-onboarding-step" aria-labelledby={`sdk-connect-step-${number}`}>
      <span className="sdk-step-number" aria-hidden="true">{number}</span>
      <div className="sdk-step-content"><h3 id={`sdk-connect-step-${number}`}>{title}</h3>{children}</div>
    </section>
  );
}

/**
 * Watches one pairing session until the application is fully connected (confirmed, machine
 * detected, telemetry received), the code expires or is cancelled, the watch limit is reached, or
 * the component unmounts. Self-scheduling (never overlapping requests), slows down after a couple
 * of minutes, and backs off on transient failures.
 */
export function usePairingConnection(pairing) {
  const [status, setStatus] = useState(null);
  const [watching, setWatching] = useState(false);
  const [timedOut, setTimedOut] = useState(false);
  const [restartKey, setRestartKey] = useState(0);
  const pairingId = pairing?.pairingId;
  const expiresAt = pairing?.expiresAt;

  useEffect(() => {
    setStatus(null);
    setTimedOut(false);
  }, [pairingId]);

  useEffect(() => {
    if (!pairingId) { setWatching(false); return undefined; }
    let cancelled = false;
    let timeoutId;
    let failures = 0;
    const startedAt = Date.now();
    setWatching(true);
    setTimedOut(false);

    const poll = async () => {
      if (cancelled) return;
      let done = false;
      try {
        const current = await sdkApi.getPairingStatus(pairingId);
        if (cancelled) return;
        failures = 0;
        const expired = current.status === 'Pending' && !current.redeemedAt && expiresAt
          && Date.now() >= new Date(expiresAt).getTime();
        setStatus(expired ? { ...current, status: 'Expired' } : current);
        if (expired || current.status === 'Expired' || current.status === 'Cancelled') done = true;
        else if (current.confirmedAt && current.connection?.machineHostName && current.connection?.lastTelemetryAt) done = true;
      } catch {
        failures += 1;
      }
      if (cancelled) return;
      if (!done && Date.now() - startedAt >= CONNECTION_WATCH_LIMIT_MS) {
        done = true;
        setTimedOut(true);
      }
      if (done) {
        setWatching(false);
        return;
      }
      const base = Date.now() - startedAt >= CONNECTION_SLOW_AFTER_MS ? CONNECTION_SLOW_POLL_MS : CONNECTION_POLL_INTERVAL_MS;
      const delay = failures > 0 ? Math.min(base * 2 ** failures, CONNECTION_MAX_BACKOFF_MS) : base;
      timeoutId = setTimeout(poll, delay);
    };

    timeoutId = setTimeout(poll, CONNECTION_POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearTimeout(timeoutId);
    };
  }, [pairingId, expiresAt, restartKey]);

  return { status, watching, timedOut, checkAgain: () => setRestartKey((k) => k + 1) };
}

function ConnectionStatus({ pairing, onTelemetry }) {
  const { status, timedOut, checkAgain } = usePairingConnection(pairing);
  const connection = status?.connection || null;
  const confirmed = Boolean(status?.confirmedAt);
  const redeemed = Boolean(status?.redeemedAt);
  const machine = connection?.machineHostName || null;
  const telemetryAt = connection?.lastTelemetryAt || null;
  const ended = status?.status === 'Expired' || status?.status === 'Cancelled';
  const fullyConnected = confirmed && machine && telemetryAt;

  const details = useMemo(() => ([
    ['Application', connection?.application],
    ['Service', connection?.service || status?.service],
    ['Environment', connection?.environment || status?.environment],
    ['Source', connection?.source],
    ['Last telemetry', telemetryAt ? new Date(telemetryAt).toLocaleString() : null]
  ].filter(([, v]) => v)), [connection, status, telemetryAt]);

  if (!pairing) {
    return (
      <p className="sdk-hint">
        Start your application with the code from step 3 and send it a normal request (for example <code>/orders</code>, not
        <code> /health</code>). Generate a pairing code first - KAIRON then detects the connection here automatically.
      </p>
    );
  }

  if (ended) {
    return (
      <div className="resolution-banner resolution-bad sdk-verify-card" role="status">
        <span className="sdk-verify-title"><IconAlertTriangle className="w-4 h-4" aria-hidden="true" />
          {status.status === 'Expired' ? 'This pairing code expired before it was used' : 'This pairing code was cancelled'}
        </span>
        <p>Generate a new pairing code in step 2.</p>
      </div>
    );
  }

  return (
    <div className={`resolution-banner ${fullyConnected ? 'resolution-good' : 'resolution-neutral'} sdk-verify-card`} role="status" aria-live="polite">
      <span className="sdk-verify-title">
        {fullyConnected
          ? <><IconCheck className="w-4 h-4" aria-hidden="true" /> Your application is connected</>
          : <><IconLink className="w-4 h-4" aria-hidden="true" /> Waiting for application…</>}
      </span>
      {!fullyConnected && (
        <p>Start your application and send it a normal request. This updates automatically.</p>
      )}
      <ul className="sdk-connection-checklist">
        <ConnectionCheck done={confirmed} label="Connected"
          detail={confirmed ? `Confirmed ${new Date(status.confirmedAt).toLocaleTimeString()}` : redeemed ? 'Code redeemed - waiting for the application to confirm' : null} />
        <ConnectionCheck done={Boolean(machine)} label="Machine detected" detail={machine} />
        <ConnectionCheck done={Boolean(telemetryAt)} label="Telemetry received"
          detail={telemetryAt ? new Date(telemetryAt).toLocaleTimeString() : null} />
      </ul>
      {details.length > 0 && (
        <dl className="sdk-connection-details">
          {details.map(([k, v]) => (
            <div key={k}><dt>{k}</dt><dd>{v}</dd></div>
          ))}
        </dl>
      )}
      {timedOut && !fullyConnected && (
        <p>
          Stopped checking after 30 minutes.{' '}
          <button type="button" className="small-btn" onClick={checkAgain}>Check again</button>
        </p>
      )}
      {fullyConnected && onTelemetry && (
        <button type="button" className="small-btn sdk-primary-action" onClick={onTelemetry}>View Live Telemetry</button>
      )}
    </div>
  );
}

function ConnectionCheck({ done, label, detail }) {
  return (
    <li className={`sdk-connection-check ${done ? 'done' : 'pending'}`}>
      <span aria-hidden="true">{done ? '✓' : '○'}</span>{' '}
      <span>{label}{done ? '' : ' - waiting'}</span>
      {detail && <span className="sdk-verify-meta"> {detail}</span>}
    </li>
  );
}
