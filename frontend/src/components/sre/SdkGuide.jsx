import React, { useEffect, useState } from 'react';
import Tabs from '../ui/Tabs';
import Badge from '../ui/Badge';
import { healthApi, telemetryApi } from '../../api';
import { IconServer, IconTerminal, IconShield, IconAlertTriangle, IconZap, IconCheck, IconLink } from '../Icons';

// ---- Code examples. Every API used here is verified against the real SDK source, not invented:
// ---- sdk/Kairon.SDK (AddKairon/UseKairon/KaironOptions/KaironPairingClient) and sdk-python
// ---- (Kairon class, kairon.middleware.KaironMiddleware, kairon.pair()).

// Primary, simplest path for a worker/console app: KaironClient redeems the pairing code itself
// and persists the resulting project credential (KaironCredentialStore.cs) - projectId/apiKey/
// endpoint are never typed in by hand. Automatic process metrics start immediately; report
// anything else the host application knows through the KaironClient instance.
const dotnetClientPairing = `using Kairon.SDK;

var kairon = new KaironClient(pairingCode: "YOUR_PAIRING_CODE");
kairon.Start();`;

const dotnetProgram = `using Kairon.SDK;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKairon(options =>
{
    options.Endpoint = Environment.GetEnvironmentVariable("KAIRON_ENDPOINT")
        ?? throw new InvalidOperationException("Set KAIRON_ENDPOINT");
    options.ProjectId = Guid.Parse(Environment.GetEnvironmentVariable("KAIRON_PROJECT_ID")
        ?? throw new InvalidOperationException("Set KAIRON_PROJECT_ID"));
    options.ApiKey = Environment.GetEnvironmentVariable("KAIRON_API_KEY")
        ?? throw new InvalidOperationException("Set KAIRON_API_KEY");
    options.ApplicationName = "OrdersApp";
    options.ServiceName = "OrdersService";
    options.Environment = Environment.GetEnvironmentVariable("KAIRON_ENVIRONMENT")
        ?? builder.Environment.EnvironmentName;
});
var app = builder.Build();
app.UseKairon(); // Before the endpoints you want to observe.
app.MapGet("/orders", () => Results.Ok(new { status = "ok" }));
app.Run();`;

const dotnetPairSnippet = `using Kairon.SDK;
var paired = await KaironPairingClient.PairAsync(
    Environment.GetEnvironmentVariable("KAIRON_ENDPOINT")!,
    Environment.GetEnvironmentVariable("KAIRON_PAIRING_CODE")!);
if (!paired.Success) throw new InvalidOperationException("Pairing failed");
// Store paired.ProjectId and paired.ApiKey in your secret store. Do not log or serialize paired.`;

// Primary, simplest path: the SDK redeems the pairing code itself and persists the resulting
// project credential (kairon/_credential_store.py) - project_id/api_key/endpoint are never
// typed in by hand.
const pythonPairing = `from kairon import Kairon

kairon = Kairon(pairing_code="YOUR_PAIRING_CODE")
kairon.start()`;

const pythonFastapiPaired = `from contextlib import asynccontextmanager
from fastapi import FastAPI
from kairon import Kairon
from kairon.middleware import KaironMiddleware

collector = Kairon(pairing_code="YOUR_PAIRING_CODE")

@asynccontextmanager
async def lifespan(app):
    collector.start()
    try:
        yield
    finally:
        collector.stop(timeout_seconds=5)

app = FastAPI(lifespan=lifespan)
app.add_middleware(KaironMiddleware, kairon=collector)

@app.get("/orders")
def orders():
    return {"status": "ok"}`;

// Explicit configuration remains fully supported - CI/CD, containers, or anyone who prefers not
// to rely on the SDK's local credential cache.
const pythonFastapi = `import os
from contextlib import asynccontextmanager
from fastapi import FastAPI
from kairon import Kairon
from kairon.middleware import KaironMiddleware

collector = Kairon(
    endpoint=os.environ["KAIRON_ENDPOINT"],
    project_id=os.environ["KAIRON_PROJECT_ID"],  # Project UUID from Pairing
    api_key=os.environ["KAIRON_API_KEY"],        # Project API key, not the pairing code
    application="OrdersApp",
    service="OrdersService",
    environment=os.environ["KAIRON_ENVIRONMENT"],
)

@asynccontextmanager
async def lifespan(app):
    collector.start()
    try:
        yield
    finally:
        collector.stop(timeout_seconds=5)

app = FastAPI(lifespan=lifespan)
app.add_middleware(KaironMiddleware, kairon=collector)

@app.get("/orders")
def orders():
    return {"status": "ok"}`;

const pythonWorker = `import os
from kairon import Kairon

collector = Kairon(
    endpoint=os.environ["KAIRON_ENDPOINT"],
    project_id=os.environ["KAIRON_PROJECT_ID"],
    api_key=os.environ["KAIRON_API_KEY"],
    application="OrdersApp",
    service="OrdersService",
    environment=os.environ["KAIRON_ENVIRONMENT"],
)

if __name__ == "__main__":
    collector.start()
    try:
        pass  # Run your application's work here.
    finally:
        collector.stop(timeout_seconds=5)`;

const pythonPairSnippet = `from kairon import pair
import os
paired = pair(os.environ["KAIRON_ENDPOINT"], os.environ["KAIRON_PAIRING_CODE"])
if paired is None:
    raise RuntimeError("Pairing failed; check connectivity, SDK type and code expiry")
# Store paired["projectId"] and paired["apiKey"] in your secret store. Do not print or log paired.`;

const configuration = `$env:KAIRON_ENDPOINT="http://127.0.0.1:8000"
$env:KAIRON_PROJECT_ID="<your-project-id>"
$env:KAIRON_API_KEY="<your-project-api-key>"
$env:KAIRON_ENVIRONMENT="Development"`;

// ---- Small shared building blocks ----------------------------------------------------------

function FlowOverview() {
  const nodes = [
    { icon: <IconServer className="w-5 h-5" />, label: 'Start KAIRON' },
    { icon: <IconLink className="w-5 h-5" />, label: 'Pair your app' },
    { icon: <IconTerminal className="w-5 h-5" />, label: 'Choose your SDK' },
    { icon: <IconCheck className="w-5 h-5" />, label: 'Run & verify' }
  ];
  return (
    <div className="sdk-flow-overview" role="img" aria-label="Start KAIRON, then pair your app, then choose your SDK, then run and verify">
      {nodes.map((n, i) => (
        <React.Fragment key={n.label}>
          {i > 0 && <span className="sdk-flow-arrow" aria-hidden="true">→</span>}
          <div className="sdk-flow-node">
            <span className="sdk-flow-node-icon" aria-hidden="true">{n.icon}</span>
            <span className="sdk-flow-node-label">{n.label}</span>
          </div>
        </React.Fragment>
      ))}
    </div>
  );
}

function FlowDiagram({ steps }) {
  return (
    <div className="sdk-flow-diagram">
      {steps.map((step, i) => (
        <React.Fragment key={step}>
          {i > 0 && <span className="sdk-flow-diagram-arrow" aria-hidden="true">↓</span>}
          <div className="sdk-flow-diagram-node">{step}</div>
        </React.Fragment>
      ))}
    </div>
  );
}

function Checklist({ items }) {
  return (
    <ul className="sdk-checklist">
      {items.map((item) => <li key={item}>{item}</li>)}
    </ul>
  );
}

function Step({ number, title, children }) {
  return (
    <section className="section-card sdk-onboarding-step" aria-labelledby={`sdk-step-${number}`}>
      <span className="sdk-step-number" aria-hidden="true">{number}</span>
      <div className="sdk-step-content"><h3 id={`sdk-step-${number}`}>{title}</h3>{children}</div>
    </section>
  );
}

function SubStep({ number, title, children }) {
  return (
    <div className="sdk-substep">
      <span className="sdk-substep-number" aria-hidden="true">{number}</span>
      <div className="sdk-substep-content"><h4>{title}</h4>{children}</div>
    </div>
  );
}

/**
 * Real verification, not a claim: polls the project's own telemetry (the same
 * api/telemetry/incidents and api/telemetry/metrics routes Live Telemetry uses) and only shows
 * "Connected" once a record has actually arrived in the last minute. An active credential alone
 * never produces this state.
 */
function VerifyCard({ projectId, onTelemetry }) {
  const [state, setState] = useState(projectId ? 'checking' : 'unknown');
  const [detail, setDetail] = useState(null);

  useEffect(() => {
    if (!projectId) { setState('unknown'); return; }
    let cancelled = false;
    const poll = async () => {
      try {
        const [incidents, metrics] = await Promise.all([
          telemetryApi.getTelemetryIncidents(projectId).catch(() => []),
          telemetryApi.getMetrics(projectId).catch(() => [])
        ]);
        if (cancelled) return;
        const freshest = [...(incidents || []), ...(metrics || [])]
          .map((r) => ({ r, t: new Date(r.timestamp).getTime() }))
          .sort((a, b) => b.t - a.t)[0];
        if (freshest && Date.now() - freshest.t < 60_000) {
          setState('connected');
          setDetail(freshest.r);
        } else {
          setState('waiting');
        }
      } catch {
        if (!cancelled) setState('waiting');
      }
    };
    poll();
    const id = setInterval(poll, 5000);
    return () => { cancelled = true; clearInterval(id); };
  }, [projectId]);

  if (state === 'connected') {
    return (
      <div className="resolution-banner resolution-good sdk-verify-card" role="status">
        <span className="sdk-verify-title"><IconCheck className="w-4 h-4" aria-hidden="true" /> Connected</span>
        <p>Your application is sending telemetry to KAIRON.</p>
        <ul>
          <li>Application: <strong>{detail?.application || '—'}</strong></li>
          <li>Service: <strong>{detail?.service || '—'}</strong></li>
          <li>Environment: <strong>{detail?.environment || '—'}</strong></li>
          <li>Recent timestamp: <span className="sdk-verify-meta">{detail ? new Date(detail.timestamp).toLocaleTimeString() : '—'}</span></li>
        </ul>
      </div>
    );
  }
  if (state === 'waiting') {
    return (
      <div className="resolution-banner resolution-neutral sdk-verify-card" role="status">
        <span className="sdk-verify-title">Waiting for telemetry…</span>
        <p>Run your application and send a normal request, such as <code>/orders</code>. This updates automatically.</p>
      </div>
    );
  }
  return (
    <div className="resolution-banner resolution-neutral sdk-verify-card" role="status">
      <span className="sdk-verify-title">Waiting for telemetry…</span>
      <p>Send a few requests to a normal application route, then check <strong>Live Telemetry</strong> for fresh timestamps and your service name.</p>
      <button type="button" className="small-btn sdk-primary-action" onClick={onTelemetry}>View Live Telemetry</button>
      <p className="sdk-hint">Health routes are excluded by default. An active API key alone does not prove telemetry is arriving.</p>
    </div>
  );
}

// ---- Per-SDK guides (identical structure: Install → Configure → Add SDK → Run → Verify) --------

function PythonGuide({ CodeBlock, projectId, onTelemetry }) {
  return (
    <div className="sdk-tab-panel">
      <p className="sdk-tab-intro">Connect a Python or FastAPI application to KAIRON.</p>
      <SubStep number="1" title="Install">
        <p>From the KAIRON repository root, with your application's virtual environment active:</p>
        <CodeBlock copyKey="python-install" code={'python -m pip install "./sdk-python[fastapi]"'} />
        <p className="sdk-hint">That installs the core SDK plus the FastAPI/Starlette middleware. Using a different framework or a plain script? Install just <code>./sdk-python</code> — see Advanced.</p>
      </SubStep>
      <SubStep number="2" title="Connect with your pairing code">
        <p>Paste the pairing code from Step 2 above. KAIRON looks up your project, its API key and its endpoint automatically — nothing else to configure.</p>
        <CodeBlock copyKey="python-pairing" code={pythonPairing} />
        <p className="sdk-hint">The SDK redeems the code once and remembers the connection, so later runs of this app don't need it again. Keep the pairing code private; never commit it to Git.</p>
        <details className="sdk-guide-details">
          <summary>Prefer explicit configuration? (CI/CD, containers)</summary>
          <p>Set these values in your application's environment instead:</p>
          <CodeBlock copyKey="python-configuration" code={configuration} />
          <ul className="sdk-config-explain">
            <li><code>KAIRON_ENDPOINT</code> — the KAIRON backend.</li>
            <li><code>KAIRON_PROJECT_ID</code> — the project UUID from Pairing.</li>
            <li><code>KAIRON_API_KEY</code> — the project telemetry key.</li>
            <li><code>KAIRON_ENVIRONMENT</code> — your application's environment.</li>
          </ul>
          <p className="sdk-hint">Explicit values always take priority over a pairing code or a stored connection. Keep your API key private. Never commit it to Git.</p>
        </details>
      </SubStep>
      <SubStep number="3" title="Add KAIRON to your application">
        <CodeBlock copyKey="python-fastapi-paired" code={pythonFastapiPaired} />
        <details className="sdk-guide-details">
          <summary>Using explicit configuration instead?</summary>
          <CodeBlock copyKey="python-fastapi-usage" code={pythonFastapi} />
        </details>
      </SubStep>
      <SubStep number="4" title="Run">
        <CodeBlock copyKey="python-run" code={'uvicorn app:app --reload'} />
        <p className="sdk-hint">Assumes the example above is saved as <code>app.py</code>. Then call a normal route, such as <code>/orders</code> — not <code>/health</code>, which KAIRON excludes from telemetry by default.</p>
      </SubStep>
      <SubStep number="5" title="Verify">
        <VerifyCard projectId={projectId} onTelemetry={onTelemetry} />
        <p className="sdk-hint">Telemetry can take a few seconds to arrive and for the page to refresh.</p>
      </SubStep>
    </div>
  );
}

function DotNetGuide({ CodeBlock, projectId, onTelemetry }) {
  return (
    <div className="sdk-tab-panel">
      <p className="sdk-tab-intro">Connect an ASP.NET Core .NET 10 application to KAIRON.</p>
      <SubStep number="1" title="Install">
        <p>From your application's release package source:</p>
        <CodeBlock copyKey="dotnet-install" code={'dotnet add package Kairon.SDK --version 1.0.1 --source "<package-feed-or-local-nupkg-folder>"'} />
        <p className="sdk-hint">There is no public NuGet feed for this release — use the package feed or local <code>.nupkg</code> folder your KAIRON release owner supplies. For local development from a source checkout instead, see Advanced.</p>
      </SubStep>
      <SubStep number="2" title="Connect with your pairing code">
        <p>Paste the pairing code from Step 2 above. KAIRON looks up your project, its API key and its endpoint automatically — nothing else to configure.</p>
        <CodeBlock copyKey="dotnet-pairing" code={dotnetClientPairing} />
        <p className="sdk-hint"><code>KaironClient</code> redeems the code once and remembers the connection, so later runs of this app don't need it again. Keep the pairing code private; never commit it to Git. Works in any .NET app — a worker, a console app, or an ASP.NET Core host.</p>
        <details className="sdk-guide-details">
          <summary>Prefer explicit configuration? (CI/CD, containers)</summary>
          <p>Set these values in your application's environment instead:</p>
          <CodeBlock copyKey="dotnet-configuration" code={configuration} />
          <ul className="sdk-config-explain">
            <li><code>KAIRON_ENDPOINT</code> — the KAIRON backend.</li>
            <li><code>KAIRON_PROJECT_ID</code> — the project UUID from Pairing.</li>
            <li><code>KAIRON_API_KEY</code> — the project telemetry key.</li>
            <li><code>KAIRON_ENVIRONMENT</code> — your application's environment.</li>
          </ul>
          <p className="sdk-hint">Explicit values always take priority over a pairing code or a stored connection. Keep your API key private. Never commit it to Git.</p>
        </details>
      </SubStep>
      <SubStep number="3" title="Add KAIRON to ASP.NET Core">
        <p>The pairing-code client above already reports process metrics automatically — enough for a worker or a quick connectivity check. For automatic per-request instrumentation in an ASP.NET Core app, register the DI-based integration instead, using the same values a pairing code resolves (shown above, or under Prefer explicit configuration):</p>
        <CodeBlock copyKey="dotnet-usage" code={dotnetProgram} />
        <p className="sdk-hint"><code>AddKairon()</code> configures telemetry. <code>UseKairon()</code> adds request monitoring.</p>
      </SubStep>
      <SubStep number="4" title="Run">
        <CodeBlock copyKey="dotnet-run" code={'dotnet run'} />
        <p className="sdk-hint">Then call a normal route, such as <code>/orders</code> — not <code>/health</code>, which KAIRON excludes from telemetry by default.</p>
      </SubStep>
      <SubStep number="5" title="Verify">
        <VerifyCard projectId={projectId} onTelemetry={onTelemetry} />
        <p className="sdk-hint">Telemetry can take a few seconds to arrive and for the page to refresh.</p>
      </SubStep>
    </div>
  );
}

// ---- Supplementary cards: pairing detail, remediation warning, security -----------------------

function PairingCard({ CodeBlock }) {
  return (
    <section className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge"><IconLink className="w-6 h-6 tone-neutral" /></div>
          <div><h3>Pair once</h3><p className="section-desc">One-time setup that gives your application a pairing code. Give the SDK only that code — it handles the project, API key and endpoint itself.</p></div>
        </div>
      </div>
      <FlowDiagram steps={['Choose project', 'Generate pairing code', 'Give the code to the SDK', 'SDK redeems it and stores the connection', 'Telemetry starts']} />
      <Checklist items={[
        'The pairing code expires after 10 minutes.',
        'The pairing code can only be used once.',
        'The SDK redeems it for a Project ID and Project API Key, and remembers the connection — later runs do not need the code again.',
        "Don't store the pairing code anywhere long-term.",
        "Don't log credentials."
      ]} />
      <details className="sdk-guide-details">
        <summary>Redeeming a pairing code manually</summary>
        <p className="sdk-hint">Most applications should just pass <code>pairing_code</code>/<code>pairingCode</code> to the SDK constructor, as shown in Step 3. Call these directly only if you need the resulting credential without starting a collector — for example, a one-time setup script.</p>
        <CodeBlock copyKey="pair-python" code={pythonPairSnippet} />
        <CodeBlock copyKey="pair-dotnet" code={dotnetPairSnippet} />
      </details>
    </section>
  );
}

function RemediationCard({ onRemediation }) {
  return (
    <section className="section-card">
      <div className="section-header">
        <div className="section-title-group">
          <div className="section-icon-badge"><IconAlertTriangle className="w-6 h-6 tone-medium" /></div>
          <div><h3>Want KAIRON to fix incidents automatically?</h3></div>
        </div>
      </div>
      <p>Connecting an SDK enables telemetry. It does not automatically give KAIRON permission to modify your machine.</p>
      <FlowDiagram steps={['Telemetry', 'Detection', 'Incident', 'AI recommendation', 'Policy authorization', 'Approval', 'Remediation', 'Verification', 'Incident resolved']} />
      <p className="sdk-hint">Windows-service remediation requires these to already be set up:</p>
      <Checklist items={[
        'An enrolled machine',
        'That machine’s project association',
        'An exact service allowlist',
        'An enabled action',
        'Executor permissions',
        'Deterministic policy authorization',
        'A configured Machine ID',
        'Audit trail',
        'Verification'
      ]} />
      {onRemediation && (
        <button type="button" className="small-btn" onClick={onRemediation}>Configure remediation →</button>
      )}
    </section>
  );
}

function SecurityCard() {
  return (
    <div className="sdk-callout sdk-callout-security">
      <IconShield className="w-5 h-5 sdk-callout-icon" aria-hidden="true" />
      <div>
        <p className="sdk-callout-title">Keep credentials private</p>
        <ul>
          <li>Never commit API keys.</li>
          <li>Never print API keys.</li>
          <li>Never put pairing codes into source code.</li>
          <li>Never use a Groq key as an application telemetry key.</li>
          <li>Never expose project credentials in screenshots or logs.</li>
        </ul>
        <p>Use environment variables or your deployment's secret manager.</p>
      </div>
    </div>
  );
}

// ---- Troubleshooting (collapsible, checklist-based) --------------------------------------------

function TroubleshootingSection() {
  return (
    <details className="section-card sdk-guide-details">
      <summary>Troubleshooting</summary>
      <div className="sdk-trouble-list">
        <div className="sdk-trouble-card">
          <h4><IconAlertTriangle className="w-4 h-4 tone-critical" aria-hidden="true" /> KAIRON isn't reachable</h4>
          <Checklist items={['KAIRON is running', 'The endpoint is correct', "You're using the backend address"]} />
          <p className="sdk-trouble-fix">Expected local backend: <code>http://127.0.0.1:8000</code></p>
        </div>
        <div className="sdk-trouble-card">
          <h4><IconAlertTriangle className="w-4 h-4 tone-critical" aria-hidden="true" /> 401 / 403</h4>
          <Checklist items={['The project ID is correct', "The project API key is correct", "The API key hasn't been revoked", "You didn't use the pairing code as the API key"]} />
          <p className="sdk-trouble-fix"><strong>Fix:</strong> generate or retrieve the correct project credential through Pairing.</p>
        </div>
        <div className="sdk-trouble-card">
          <h4><IconAlertTriangle className="w-4 h-4 tone-critical" aria-hidden="true" /> No telemetry appears</h4>
          <Checklist items={['The application is running', 'The SDK is initialized', 'The middleware is registered', 'You sent requests to a normal route', 'You waited several seconds', 'Live Telemetry is refreshed']} />
          <p className="sdk-trouble-fix">Health and metrics routes may be excluded by default.</p>
        </div>
        <div className="sdk-trouble-card">
          <h4><IconAlertTriangle className="w-4 h-4 tone-critical" aria-hidden="true" /> Pairing failed</h4>
          <Checklist items={["You chose the correct SDK type", "The code hasn't expired", "The code hasn't already been used"]} />
          <p className="sdk-trouble-fix"><strong>Fix:</strong> generate a new pairing code.</p>
        </div>
        <div className="sdk-trouble-card">
          <h4><IconAlertTriangle className="w-4 h-4 tone-critical" aria-hidden="true" /> SDK package/import not found</h4>
          <p className="sdk-hint">Python:</p>
          <Checklist items={['The correct virtual environment is active', 'The SDK is installed into that environment']} />
          <p className="sdk-hint">.NET:</p>
          <Checklist items={['The correct package source', 'The correct SDK version', 'The correct project reference']} />
        </div>
      </div>
    </details>
  );
}

// ---- Advanced (collapsible; deep material only — never required for the main path) ------------

function AdvancedSection({ CodeBlock }) {
  return (
    <details className="section-card sdk-guide-details sdk-advanced">
      <summary>Advanced</summary>
      <div className="sdk-get-started">
        <section className="section-card">
          <h3>Networking</h3>
          <FlowDiagram steps={['Your application', 'KAIRON backend — 127.0.0.1:8000']} />
          <p>Port 8000 is the backend. Port 8001 is the AI service. Port 5173 is the frontend development server. Don't configure the SDK against 8001 or 5173.</p>
          <p>For a remote application or one running in a container, <code>127.0.0.1</code> means that application's own host — not this machine. The desktop backend binds to loopback by default. Remote collection needs an explicitly configured, reachable backend with HTTPS and appropriate network access; changing the SDK URL alone does not expose KAIRON externally.</p>
          <p className="sdk-hint">Prerequisites: .NET SDK integration targets ASP.NET Core on .NET 10; Python requires 3.9 or newer. The Python core has no third-party runtime dependencies; FastAPI examples also need FastAPI and an ASGI server.</p>
        </section>
        <section className="section-card">
          <h3>Alternative installs</h3>
          <p><strong>.NET</strong> — for a source checkout instead of a package feed, reference the SDK project using its real path:</p>
          <CodeBlock copyKey="dotnet-source" code={'dotnet add reference "<KAIRON-repository>/sdk/Kairon.SDK/Kairon.SDK.csproj"'} />
          <p><strong>Python</strong> — for a source checkout, run from the KAIRON repository root. Choose the core or middleware variant; editable installs are for SDK development, not production:</p>
          <CodeBlock copyKey="python-source" code={'python -m pip install ./sdk-python\n# Or, for FastAPI/Starlette middleware:\npython -m pip install "./sdk-python[fastapi]"'} />
          <p>A release wheel is also supplied with some SDK releases:</p>
          <CodeBlock copyKey="python-wheel-install" code={'python -m pip install "<path-to-kairon-sdk-wheel.whl>"'} />
          <p><strong>Plain Python, no FastAPI</strong> — for a worker or script, create one collector per process after it starts:</p>
          <CodeBlock copyKey="python-worker" code={pythonWorker} />
        </section>
        <section className="section-card">
          <h3>Delivery and shutdown</h3>
          <p>Both SDKs use a bounded in-memory queue (default 1,000 items) and best-effort delivery, without a disk spool or automatic retries. Queue pressure drops the oldest items. A collector outage should not block application requests, but telemetry can be lost.</p>
          <p>Python exposes <code>delivered_count</code>, <code>failed_count</code>, <code>dropped_count</code> and <code>pending_count</code>; .NET exposes corresponding PascalCase members on <code>IKaironTelemetryQueue</code>. Pending counts exclude in-flight sends. Stop producers before a final drain: Python <code>stop(timeout_seconds=5)</code> and .NET <code>FlushAsync(token)</code> return false after a lifetime failure/drop or an incomplete drain. The .NET hosted sender also attempts a bounded shutdown drain. Queue emptiness alone is not delivery confirmation.</p>
          <p>Metrics intervals default to 10 seconds (.NET) and 5 seconds (Python).</p>
        </section>
        <section className="section-card">
          <h3>Remediation requirements and Machine ID</h3>
          <p>Configure <code>MachineId</code> (.NET) or <code>machine_id</code> (Python) to the real enrolled machine UUID for scoped evidence. Never invent a machine ID or assume an SDK service label grants Service Control Manager access.</p>
          <p>Environments like Development, Staging and Production describe the monitored application's own workload; they don't enable a KAIRON execution mode or grant permission. The supported boundary is one backend and one executor; multi-replica remediation is not supported.</p>
        </section>
        <section className="section-card">
          <h3>Production and diagnostics</h3>
          <p>Before sharing diagnostics, remove credentials, pairing codes and sensitive application payloads. Keep request/response body capture disabled unless explicitly required and reviewed.</p>
        </section>
      </div>
    </details>
  );
}

// ---- Page ----------------------------------------------------------------------------------

export default function SdkGuide({ CodeBlock, onPairing, onTelemetry, onRemediation, projectId }) {
  const [platform, setPlatform] = useState('python');
  const [checking, setChecking] = useState(false);
  const [reachable, setReachable] = useState(null); // null = unknown, true/false once checked

  async function checkConnection() {
    setChecking(true);
    try {
      await healthApi.getHealth();
      setReachable(true);
    } catch {
      setReachable(false);
    } finally {
      setChecking(false);
    }
  }

  useEffect(() => { checkConnection(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div className="sdk-get-started sdk-onboarding">
      <header className="sdk-onboarding-intro">
        <h3>Connect your application</h3>
        <p>Connect your app to KAIRON in a few minutes.</p>
      </header>

      <FlowOverview />

      <Step number="1" title="Start KAIRON">
        <p>Make sure KAIRON is running.</p>
        <p className="sdk-backend-address">Backend: <code>http://127.0.0.1:8000</code></p>
        <p role="status" className={`sdk-connection-status ${reachable === true ? 'online' : reachable === false ? 'offline' : ''}`}>
          <span className={`status-dot ${reachable === true ? 'online' : reachable === false ? 'offline' : ''}`} aria-hidden="true" />
          {reachable === true && 'KAIRON is running'}
          {reachable === false && 'KAIRON is not reachable — start KAIRON and try again.'}
          {reachable === null && (checking ? 'Checking…' : 'Not checked yet.')}
        </p>
        <button type="button" className="small-btn sdk-primary-action" disabled={checking} onClick={checkConnection}>{checking ? 'Checking…' : 'Check Connection'}</button>
      </Step>

      <Step number="2" title="Pair your application">
        <p>Create a project and generate a one-time pairing code. Give that code to the SDK — it looks up the project, API key and endpoint for you.</p>
        <Checklist items={[
          'The pairing code expires after 10 minutes.',
          'The pairing code is single-use.',
          'The pairing code is only used during setup — the SDK stores what it needs and does not require it again.',
          'Application telemetry uses the resulting Project ID + Project API Key, resolved automatically from the code.',
          'Never use the pairing code as the telemetry API key.'
        ]} />
        <button type="button" className="small-btn sdk-primary-action" onClick={onPairing}>Open Pairing</button>
      </Step>

      <Step number="3" title="Choose your SDK">
        <Tabs items={[{ id: 'python', label: '🐍 Python' }, { id: 'dotnet', label: '🔷 .NET' }]} activeId={platform} onChange={setPlatform} />
        {platform === 'python'
          ? <PythonGuide CodeBlock={CodeBlock} projectId={projectId} onTelemetry={onTelemetry} />
          : <DotNetGuide CodeBlock={CodeBlock} projectId={projectId} onTelemetry={onTelemetry} />}
      </Step>

      <PairingCard CodeBlock={CodeBlock} />
      <RemediationCard onRemediation={onRemediation} />
      <SecurityCard />
      <TroubleshootingSection />
      <AdvancedSection CodeBlock={CodeBlock} />
    </div>
  );
}
