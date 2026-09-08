import React, { useState } from 'react';
import Tabs from '../ui/Tabs';
import { healthApi } from '../../api';

const dotnet = `using Kairon.SDK;

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

const python = `import os
from kairon import Kairon

def create_collector():
    return Kairon(
        endpoint=os.environ["KAIRON_ENDPOINT"],
        project_id=os.environ["KAIRON_PROJECT_ID"],  # Real project UUID
        api_key=os.environ["KAIRON_API_KEY"],       # Project key, not Groq key
        application="OrdersApp",
        service="OrdersService",
        environment=os.environ["KAIRON_ENVIRONMENT"],
    )

# For a worker, create one collector per process after the worker starts.
if __name__ == "__main__":
    collector = create_collector()
    collector.start()
    try:
        # Run your application's work here.
        pass
    finally:
        if not collector.stop(timeout_seconds=5):
            # Report a delivery warning through your application's diagnostics.
            # Never include credentials or the collector configuration.
            print("KAIRON telemetry drain incomplete")`;

const fastapi = `# Save create_collector() from the Python example in telemetry.py.
from contextlib import asynccontextmanager
from fastapi import FastAPI
from kairon.middleware import KaironMiddleware
from telemetry import create_collector

collector = create_collector()

@asynccontextmanager
async def lifespan(app):
    collector.start()
    try:
        yield
    finally:
        if not collector.stop(timeout_seconds=5):
            print("KAIRON telemetry drain incomplete")

app = FastAPI(lifespan=lifespan)
app.add_middleware(KaironMiddleware, kairon=collector)

@app.get("/orders")
def orders():
    return {"status": "ok"}`;

function AdvancedGuide({ CodeBlock }) {
  return <div className="sdk-get-started">
    <section className="section-card">
      <h3>Networking and prerequisites</h3>
      <p>The SDK sends application telemetry to KAIRON. It does not need a Groq key, database connection, or permission to restart services.</p>
      <ol>
        <li>Keep KAIRON running and choose a backend address reachable from your application.</li>
        <li>Create or select a project in Pairing, then redeem a code for a project credential.</li>
        <li>Install the matching SDK and configure the endpoint, project UUID and project API key.</li>
        <li>Send normal application traffic and confirm fresh telemetry before enabling any remediation.</li>
      </ol>
      <h4>Choose the right address</h4>
      <p>For an application on the same Windows host, the installed backend uses <code>http://127.0.0.1:8000</code>. Use its base URL, without <code>/api</code>. Port 8001 is the AI service; port 5173 is a development frontend.</p>
      <p>Inside a container or on another machine, localhost refers to that application’s own host. The desktop backend binds to loopback by default. Remote collection needs an explicitly configured, reachable backend with HTTPS and appropriate network access; changing the SDK URL alone does not expose the desktop backend.</p>
      <p>Prerequisites: .NET SDK integration targets ASP.NET Core on .NET 10; Python requires 3.9 or newer. The Python core has no third-party runtime dependencies. FastAPI examples also need FastAPI and an ASGI server in the host application.</p>
    </section>
    <section className="section-card">
      <h3>Pair once; keep the project key private</h3>
      <ol>
        <li>In Pairing, choose a project and the correct SDK type. Generate a code; it expires after 10 minutes and can be used once.</li>
        <li>Redeem it from a trusted setup process using the matching SDK call below. Check the result before configuring the app.</li>
        <li>Store the returned project ID and API key in your deployment’s protected configuration. The pairing call does not persist them for you. Do not print or commit the returned object.</li>
        <li>Configure the SDK from that protected configuration at every startup. Do not redeem the same code on each application restart.</li>
      </ol>
      <CodeBlock copyKey="pair-python" code={`from kairon import pair
import os
paired = pair(os.environ["KAIRON_ENDPOINT"], os.environ["KAIRON_PAIRING_CODE"])
if paired is None:
    raise RuntimeError("Pairing failed; check connectivity, SDK type and code expiry")
# Securely store paired["projectId"] and paired["apiKey"] using your secret store.
# Keep the endpoint reachable from the application; a returned loopback URL may be local-only.`} />
      <CodeBlock copyKey="pair-dotnet" code={`using Kairon.SDK;
var paired = await KaironPairingClient.PairAsync(
    Environment.GetEnvironmentVariable("KAIRON_ENDPOINT")!,
    Environment.GetEnvironmentVariable("KAIRON_PAIRING_CODE")!);
if (!paired.Success) throw new InvalidOperationException("Pairing failed");
// Securely store paired.ProjectId and paired.ApiKey using your secret store.
// Do not log or serialize paired.`} />
      <p>Examples below read environment variables supplied by your deployment. They are explicit example wiring, not automatic SDK discovery. Use the real project UUID, not a project name. Never put a Groq key or operator key in <code>KAIRON_API_KEY</code>. Revoke a compromised project credential in Pairing and pair again to replace it.</p>
    </section>
    <section className="section-card">
      <h3>.NET SDK</h3>
      <p>From your application project directory, install the release package from the package feed supplied by your KAIRON release owner. Public registry availability is not assumed.</p>
      <CodeBlock copyKey="dotnet-install" code={'dotnet add package Kairon.SDK --version 1.0.1 --source "<package-feed-or-local-nupkg-folder>"'} />
      <p>For a source checkout instead, reference the SDK project using its real path:</p>
      <CodeBlock copyKey="dotnet-source" code={'dotnet add reference "<KAIRON-repository>/sdk/Kairon.SDK/Kairon.SDK.csproj"'} />
      <p>Minimal ASP.NET Core <code>Program.cs</code>; merge the registration and middleware into your existing application rather than replacing its routes.</p>
      <CodeBlock copyKey="dotnet-advanced-usage" code={dotnet} />
    </section>
    <section className="section-card">
      <h3>Python SDK</h3>
      <p>Activate your application’s virtual environment. Install the wheel supplied with your SDK release, replacing the placeholder with its actual path:</p>
      <CodeBlock copyKey="python-install" code={'python -m pip install "<path-to-kairon-sdk-wheel.whl>"'} />
      <p>For a source checkout, these commands run from the KAIRON repository root. Choose the core or middleware variant; editable installs are for SDK development, not a required production setup.</p>
      <CodeBlock copyKey="python-source" code={'python -m pip install ./sdk-python\n# Or, for FastAPI/Starlette middleware:\npython -m pip install "./sdk-python[fastapi]"'} />
      <p>Minimal worker/script usage; merge the collector startup and shutdown into your existing application rather than replacing its routes.</p>
      <CodeBlock copyKey="python-advanced-usage" code={python} />
      <details className="sdk-guide-details"><summary>FastAPI: startup, middleware and shutdown</summary>
        <p className="sdk-hint">The SDK extra supplies Starlette; install FastAPI and your ASGI server separately if the application does not already include them. Save this example as <code>app.py</code> and run it with your normal ASGI server. Initialize a collector in each worker process, not in a parent process before forking.</p>
        <CodeBlock copyKey="python-fastapi-advanced" code={fastapi} />
      </details>
    </section>
    <section className="section-card">
      <h3>Verify delivery before troubleshooting detection</h3>
      <ol>
        <li>Send a few successful requests to your app’s <code>/orders</code> route or another real route. The default exclusions include <code>/health</code>, <code>/healthz</code>, <code>/metrics</code> and <code>/favicon.ico</code>.</li>
        <li>Allow at least one metrics interval: .NET defaults to 10 seconds; Python defaults to 5 seconds. Allow additional time for the UI to refresh.</li>
        <li>In Live telemetry and Services, check recent timestamps and the exact project, application, service and environment. An active credential or historical incident is not proof of current delivery.</li>
        <li>Only investigate incident detection after confirming fresh telemetry. Normal traffic need not create an incident. Do not overload a production application merely to trigger a detector.</li>
      </ol>
      <h4>Delivery and shutdown guarantees</h4>
      <p>Both SDKs use a bounded in-memory queue (default 1,000 items) and best-effort delivery, without a disk spool or automatic retries. Queue pressure drops the oldest items. Collector outages should not block application requests, but telemetry can be lost.</p>
      <p>Python exposes <code>delivered_count</code>, <code>failed_count</code>, <code>dropped_count</code> and <code>pending_count</code>; .NET exposes corresponding PascalCase members on <code>IKaironTelemetryQueue</code>. Pending counts exclude in-flight sends. Stop producers before a final drain. Python <code>stop(timeout_seconds=5)</code> and .NET <code>FlushAsync(token)</code> return false after a lifetime failure/drop or incomplete drain. The .NET hosted sender also attempts a bounded shutdown drain. Queue emptiness alone is not delivery confirmation.</p>
    </section>
    <section className="section-card">
      <h3>Troubleshooting</h3>
      <dl>
        <dt>Connection refused or timeout</dt><dd>Check that KAIRON is running, the base URL points to the backend, and the application can reach it from its own host/container. Check DNS, firewall and TLS configuration. Do not disable certificate validation to hide a failure.</dd>
        <dt>401 or 403 / no authenticated telemetry</dt><dd>Check the project API key, its revocation status and the project UUID. Pairing codes are not telemetry keys. Remediation-scoped telemetry also requires a matching enrolled machine and credential association.</dd>
        <dt>Pairing rejected</dt><dd>Check SDK type, expiry and whether the code was already used. Generate a new code; never paste secrets into logs or support screenshots.</dd>
        <dt>No requests or metrics appear</dt><dd>Check middleware placement, Python startup, telemetry/metrics toggles, ignored paths, service filters and timestamps. Inspect delivery counters. Installing the package alone does not start collection.</dd>
        <dt>Package not found / import fails</dt><dd>Confirm the release package source and version, Python virtual environment, and actual SDK path. FastAPI middleware is a separate import. The desktop installer is not a Python wheel or NuGet SDK package.</dd>
        <dt>Connected app, but no remediation</dt><dd>Telemetry collection does not authorize execution. Inspect the incident, recommendation, policy decision, approval state and audit trail; see the requirements below.</dd>
      </dl>
    </section>
    <section className="section-card">
      <h3>Windows-service remediation is a separate setup</h3>
      <p>Use Development, Staging or Production for KAIRON product/runtime environments. External application telemetry labels describe the monitored workload; they do not enable a product execution mode or grant permission.</p>
      <p>Windows-service remediation requires an explicitly enrolled machine, its project association, exact service allowlist, enabled action, executor permissions and deterministic policy authorization. Configure <code>MachineId</code> (.NET) or <code>machine_id</code> (Python) to the real enrolled machine UUID for scoped evidence. Never invent a machine ID or assume an SDK service label grants SCM access.</p>
      <p>The lifecycle remains detection → incident → AI recommendation → deterministic policy and authorization → typed execution → fresh evidence for the exact machine/service → verification → resolution and audit. AI configuration belongs in Settings. Successful execution alone is not verified recovery. The supported boundary is one backend and one executor; multi-replica remediation is not supported.</p>
      <p>Before sharing diagnostics, remove credentials, pairing codes and sensitive application payloads. Keep request/response body capture disabled unless explicitly required and reviewed.</p>
    </section>
  </div>;
}


const configuration = `$env:KAIRON_ENDPOINT="http://127.0.0.1:8000"
$env:KAIRON_PROJECT_ID="<your-project-id>"
$env:KAIRON_API_KEY="<your-project-api-key>"
$env:KAIRON_ENVIRONMENT="Development"`;

function Step({ number, title, children }) {
  return <section className="section-card sdk-onboarding-step" aria-labelledby={`sdk-step-${number}`}>
    <span className="sdk-step-number" aria-hidden="true">{number}</span>
    <div className="sdk-step-content"><h3 id={`sdk-step-${number}`}>{title}</h3>{children}</div>
  </section>;
}

export default function SdkGuide({ CodeBlock, onPairing, onTelemetry }) {
  const [platform, setPlatform] = useState('python');
  const [checking, setChecking] = useState(false);
  const [connection, setConnection] = useState('');
  async function checkConnection() {
    setChecking(true); setConnection('');
    try {
      await healthApi.getHealth();
      setConnection('KAIRON backend is reachable from this page.');
    } catch {
      setConnection('Cannot reach KAIRON. Open the app and try again.');
    } finally { setChecking(false); }
  }
  return <div className="sdk-get-started sdk-onboarding">
    <header className="sdk-onboarding-intro">
      <h3>Connect your application</h3>
      <p>Pair once, add the SDK, and see your application’s telemetry.</p>
      <p className="sdk-hint">Your application does not need direct database access.</p>
    </header>
    <Step number="1" title="Start KAIRON">
      <p>Keep KAIRON running while your application sends telemetry.</p>
      <p className="sdk-backend-address">Local backend <code>http://127.0.0.1:8000</code></p>
      <button type="button" className="small-btn sdk-primary-action" disabled={checking} onClick={checkConnection}>{checking ? 'Checking…' : 'Check Connection'}</button>
      {connection && <p role="status" className="sdk-hint">{connection}</p>}
    </Step>
    <Step number="2" title="Connect a project">
      <p>Pairing is a one-time setup step that gives your application its Project ID and Project API Key.</p>
      <p className="sdk-hint">Create or select a project in Pairing. The code is single-use and expires in 10 minutes. Use the resulting API key—not the pairing code—for telemetry.</p>
      <button type="button" className="small-btn sdk-primary-action" onClick={onPairing}>Open Pairing</button>
      <p className="sdk-hint">Need help redeeming the code? Expand Advanced / Troubleshooting below.</p>
    </Step>
    <Step number="3" title="Install the SDK">
      <Tabs items={[{id:'python',label:'Python'},{id:'dotnet',label:'.NET'}]} activeId={platform} onChange={setPlatform} />
      <p>{platform === 'python' ? 'From the KAIRON repository root, with your application’s virtual environment active:' : 'From your ASP.NET Core application directory, using your release package source:'}</p>
      <CodeBlock copyKey={`${platform}-quick-install`} code={platform === 'python' ? 'python -m pip install "./sdk-python[fastapi]"' : 'dotnet add package Kairon.SDK --version 1.0.1 --source "<package-feed-or-local-nupkg-folder>"'} />
      <p className="sdk-hint">{platform === 'python' ? 'Python 3.9+. A FastAPI application also needs FastAPI and its ASGI server.' : 'Requires an ASP.NET Core application targeting .NET 10.'} Release-wheel and source-reference alternatives are available below.</p>
    </Step>
    <Step number="4" title="Configure and run">
      <p>Set these values in your application’s environment. Replace the placeholders with the credentials from pairing.</p>
      <CodeBlock copyKey="configuration" code={configuration} />
      <p className="sdk-hint">PowerShell example. Keep your API key private; never commit it. For a remote application, use a backend address it can reach.</p>
      <details className="sdk-guide-details" key={platform}>
        <summary>{platform === 'python' ? 'Show Python integration example' : 'Show .NET integration example'}</summary>
        <CodeBlock copyKey={`${platform}-usage`} code={platform === 'python' ? python : dotnet} />
        {platform === 'python' && <details className="sdk-guide-details"><summary>FastAPI startup and middleware</summary><CodeBlock copyKey="python-fastapi" code={fastapi} /></details>}
      </details>
      <p>Add the integration shown above, then run your application normally.</p>
    </Step>
    <Step number="5" title="Verify telemetry">
      <p>Send a few requests to a normal application route, such as <code>/orders</code>. Wait 5–10 seconds plus the page refresh, then look for fresh timestamps and your service name.</p>
      <button type="button" className="small-btn sdk-primary-action" onClick={onTelemetry}>View Live Telemetry</button>
      <p className="sdk-hint">Health routes are excluded by default. An active API key alone does not prove telemetry is arriving.</p>
    </Step>
    <details className="section-card sdk-guide-details sdk-advanced">
      <summary>Advanced / Troubleshooting</summary>
      <p className="sdk-hint">Pairing code examples, remote networking, alternative installs, delivery behavior, troubleshooting and remediation requirements.</p>
      <AdvancedGuide CodeBlock={CodeBlock} />
    </details>
  </div>;
}
