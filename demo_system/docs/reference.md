# Checkout demo reference

Start with the [demo README](../README.md) for deployment and fault commands.
This guide explains the signals, timing, readiness checks, and local development
tools.

## Signals and expected behavior

The authoritative queries and thresholds are in
[config/signals.json](../config/signals.json). The generated configuration
evaluates all four signals every minute. Missing data produces `Unknown`, and
threshold comparisons are strict.

| Entity / signal | Unit | Degraded | Unhealthy |
| --- | --- | --- | --- |
| `checkout-api` / `p95-latency` | milliseconds | >500 | >1000 |
| `checkout-api` / `checkout-error-rate` | percent | >5 | >10 |
| `checkout-api` / `availability` | scrape `up` (1/0) | Not configured | <1 |
| `checkout-traffic` / `traffic-freshness` | seconds | >30 | >60 |

Traffic freshness measures the **sample timestamp minus last-request time**,
not query time minus last-request time. Ingestion delay does not inflate that
business age.

Separate 120-second sample-age guards reject stale `up` and last-request
samples. They do not change the 30/60-second traffic thresholds. Latency,
error rate, and traffic freshness also require a fresh `up == 1`. A fresh
failed scrape makes availability `Unhealthy` and the other inputs `Unknown`.
Missing or non-finite inputs do not become fabricated zero or healthy values.

The model groups the three API signals in `api-signals` and traffic freshness
in `traffic-signals`, each using WorstOf aggregation. The parent `checkout`
entity aggregates its API and traffic dependencies with WorstOf, and the model's
root entity depends on `checkout`, so the demo system's health is the worst of
the checkout API and checkout traffic entities. External
signals appear on the **first evaluator report**; unresolved members before
that report are not proof of health.

### Fault responses and timing

| Mode | Valid checkout response |
| --- | --- |
| `healthy` | 200 after about 50 ms |
| `slow` | 200 after about 750 ms |
| `very-slow` | 200 after about 1500 ms |
| `errors` | 500 for every valid checkout |

The API starts healthy on each restart. Faults stay active until changed or
the API restarts. They never cycle automatically.

Latency and error queries use **five-minute rolling windows**, so detection and
recovery are not immediate. Histogram p95 is a bucket estimate, not the exact
synthetic delay. Startup, identity-role propagation (potentially around
30 minutes for publishing), metric ingestion, and one-minute evaluation add
further delay. Returning to healthy does not immediately clear earlier samples.

### Traffic generation

The traffic generator makes one immediate attempt, then waits the configured
interval **after each attempt finishes**, including errors and timeouts. Its
HTTP timeout is three seconds. Requests are serial, with no retries, bursts,
parallel sends, or catch-up traffic.

Probes and Prometheus scrapes are not checkout traffic. Intervals above the fixed
30/60-second freshness thresholds can intentionally show degraded or unhealthy
traffic between requests.

## Readiness and troubleshooting

`GET /healthz` stays ready during business faults. Successful scrapes can still
have `up = 1` while every valid checkout returns 500. Readiness means the
application is running, not that checkout behavior is correct.

ACI uses lightweight HTTP readiness and liveness probes, without starting a
new .NET process for every probe. Deployment checks events from the current
container incarnation at whole-second precision; warnings in its start second
count as current. Probe warnings named `Failed` are transient only while the
container is Running with zero restarts. Any restart fails deployment, and the
private application readiness marker is still required. Old warnings do not
invalidate later deployments.

`DEMO_READY` does **not** confirm metric ingestion or health-report acceptance.
Inspect Prometheus and ACI logs, run the queries in
[signals.json](../config/signals.json) against the workspace, and check
evaluator/report results separately. Follow the
[evaluator verification guide](evaluator-setup.md#5-verify-the-results) before
claiming end-to-end health.

## Local development and validation

The demo has its own [test script](../scripts/test.ps1); it does not invoke the
root test, solution, or deployment workflow. Local checks do not need Docker
or Azure access.

Install these tools explicitly:

- The exact .NET SDK in [global.json](../../global.json).
- PowerShell **7.4+**.
- Standalone **Bicep 0.48.1**, including the offline `snapshot` command.
- An installed, importable **PSScriptAnalyzer** module, verified with 1.24.0.

From the repository root, restore only the demo test project and run its checks:

```powershell
dotnet restore .\demo_system\tests\Checkout.Demo.Tests\Checkout.Demo.Tests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Demo restore failed' }
& .\demo_system\scripts\test.ps1
if ($LASTEXITCODE -ne 0) { throw 'Demo validation failed' }
```

The test script uses `--no-restore` and installs nothing. If assets or packages
are missing, explicitly restore that project and rerun.

### Tool paths

The explicit `-BicepPath` parameter takes precedence over `BICEP_CLI_PATH`,
which takes precedence over PATH. For tools outside the usual search paths:

```powershell
$env:BICEP_CLI_PATH = 'C:\tools\bicep.exe'
# For C:\tools\PSScriptAnalyzer\1.24.0\PSScriptAnalyzer.psd1:
$env:PSModulePath = 'C:\tools' + [IO.Path]::PathSeparator + $env:PSModulePath
& .\demo_system\scripts\test.ps1 -BicepPath $env:BICEP_CLI_PATH
```

Use your actual trusted paths. Add the module's parent directory, not its
version directory, so child processes can import it.

### What the checks cover

The script parses and analyzes demo source PowerShell, failing on analyzer
warnings or errors. It excludes generated `artifacts`, `bin`, and `obj`
directories, compiles both demo Bicep templates, and runs
`Checkout.Demo.Tests` in Release.

The tests cover the checkout API, metrics and fault modes, the traffic options
and runner, the CLI commands, evaluator signal configuration (threshold
boundaries and nonfinite observations), and offline Bicep snapshots of both
templates. The deploy, remove and set-fault scripts are only analyzed by
PSScriptAnalyzer; no automated test runs them against real or substituted Azure
or Docker commands.

Compiled templates and `demo.trx` evidence remain under
`demo_system\artifacts\validation\<invocation-id>`. Success requires nonempty
results with every discovered test executed and passed, with no failures or
skips. These checks do not replace live acceptance after changes to images,
templates, permissions, or the target environment.

A demo-only live check should cover image build/push, all three containers,
identity-authenticated metric ingestion, traffic, fault modes, recovery, and
ownership-checked teardown. Verifying evaluator reports and resulting model
health requires the [separate evaluator setup](evaluator-setup.md).
