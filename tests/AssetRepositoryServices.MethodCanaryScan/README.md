# Method canary scan (AB#7118)

Evidence from the **running services**, not only from the in-process tests of octo-common-services
(`CkMethodRedactionBusTests`, AB#6914/AB#7015): a sensitive method parameter and the caller's access token reach none of

| Sink | How the scan reads it |
|------|-----------------------|
| GraphQL answers | the raw body (and `WWW-Authenticate`) of every call the scan makes |
| Service log files | the bytes every `*.log` in the lane's `logFiles/` folder (`Start-Octo`) gained during the scan |
| Audit / event log | the System.Notification events of the scan tenant created during the scan (`Method.Invocation` and all others) |
| Exported traces, metrics, logs | the bytes a local OpenTelemetry collector with the file exporter wrote during the scan |
| Handling service of the asset-repo method | log lines (message, exception, scopes) and activities (all sources, all tags) of the scan's handler host |

Searched values: the canaries of AB#7015 (`CANARY-SECRET-123` and an escaping canary with quote, backslash, `+&<>'/`,
non-ASCII letters, a surrogate pair and control characters) in every form a serializer or URL produces (`Canaries.Forms`:
plain, System.Text.Json default and relaxed, Newtonsoft, lower-case hex, JSON-in-JSON, percent-encoded), the access tokens
the scan uses (whole token and signature), a forged bearer token and the secrets of the scan's clients.

## What it calls

Both scans call the **typed method mutation** of the asset repository (AB#5670/AB#7026), through the gateway, the
dispatcher and the bus:

- `AssetRepositoryMethod_CanaryReachesNoSink`: `MethodCanaryScan/CanaryAccount.Rotate-1` of the test model
  `tests/MethodCanaryScanCkModel` (never published, `OctoPublishCkModel=false`), handled by the scan's own handler host
  (`ScanHandlerHost`: `AddCkMethodHandlers`, caller token validation against the local identity service, the compiled scan
  model as definition source). Cases for each canary: success, handler failure (exception text with the secret and the
  token, also JSON-escaped), timeout (`timeoutSeconds: 3`, handler hangs), validation errors (handler, GraphQL input of the
  wrong shape, unknown input member), a declared error, refused calls (`Guarded-1` declares a role nobody holds, a caller
  without roles, a forged token).
- `IdentityMethod_CanaryReachesNoSink`: `System.Identity/User.CreateUser-1` (sensitive `password`), hosted by the identity
  service (needs `Methods:Identity:EnablePasswordMethods=true`, the local default). Cases: new user, user exists, password
  policy violation, GraphQL input of the wrong shape, caller without roles, forged token. Not covered: `METHOD_TIMEOUT`
  (cannot be forced without a fault injection in the identity service; the timeout path of gateway and dispatcher is
  covered by the asset-repo scan).

Every case also asserts its outcome (`SUCCESS`, `METHOD_HANDLER_FAILED`, `METHOD_TIMEOUT`, `METHOD_FORBIDDEN`, ...), so a
clean result cannot come from calls that never reached the path.

## Run it

Not part of the CI run: the project is not named `*Tests` and every scan test skips unless `OCTO_METHOD_CANARY_SCAN=1`.
`CaptureLoggerScopeTests` run without the stack: they check that the handler-host capture logger keeps its log scopes per
async flow (an immutable scope stack, so parallel handler calls never see or remove each other's scopes).

1. Local OpenTelemetry collector with the file exporter (output folder of your choice):

   ```bash
   OUT=/tmp/octo-canary-otel; mkdir -p $OUT && chmod 777 $OUT
   docker run -d --name octo-canary-otel -p 4317:4317 -p 4318:4318 -v $OUT:/out \
     -v $PWD/tests/AssetRepositoryServices.MethodCanaryScan/otel-collector.yaml:/etc/otelcol-contrib/config.yaml \
     otel/opentelemetry-collector-contrib:latest
   ```

2. Start the stack so that the services export to it (`OTLP_ENDPOINT_URL` of `ObservabilityBuilder`):

   ```powershell
   $env:OCTO_OTLP_ENDPOINT_URL = 'http://localhost:4317'
   Start-Octo -branch main -configuration DebugL -nonInteractive $true
   ```

3. Scratch tenant with the scan model (client-credentials client with `-apic` and `TenantManagement`, see the octo skill
   `references/temp-tenants.md`; secrets only through the environment):

   ```bash
   export OCTO_CLI_CLIENT_ID=claude-agent OCTO_CLI_CLIENT_SECRET="$(cat ~/.octo-cli/claude-agent.secret)"
   octo-cli -c Create -tid tmp-canary-scan -db tmp-canary-scan --scratch --context <your octosystem client-credentials context>
   octo-cli -c AddContext -n local_tmp-canary-scan -isu https://localhost:5003/ -asu https://localhost:5001/ -tid tmp-canary-scan
   octo-cli -c LogInClientCredentials --context local_tmp-canary-scan
   dotnet build tests/MethodCanaryScanCkModel -c DebugL
   octo-cli -c ImportCk -w --context local_tmp-canary-scan \
     -f tests/MethodCanaryScanCkModel/bin/DebugL/net10.0/octo-ck-libraries/MethodCanaryScanCkModel/out/ck-methodcanaryscan.yaml
   ```

4. The scan (one command):

   ```bash
   OCTO_METHOD_CANARY_SCAN=1 OCTO_CANARY_TENANT=tmp-canary-scan OCTO_CANARY_OTLP_DIR=$OUT \
     dotnet test tests/AssetRepositoryServices.MethodCanaryScan -c DebugL
   ```

   The reports `method-canary-scan-asset-repo.md` and `method-canary-scan-identity.md` (calls, sinks with the amount
   scanned, hits with an excerpt in which every secret and every JWT part is replaced by a label) are written to
   `OCTO_CANARY_RESULT_DIR` (default: the test output folder). A run fails on a hit, on a case with an unexpected outcome
   and when no telemetry arrived (accept that explicitly with `OCTO_CANARY_ALLOW_NO_OTLP=1`).

5. Clean up: `octo-cli -c Delete -tid tmp-canary-scan --disable-features -y --context <octosystem context>`,
   `docker rm -f octo-canary-otel`. The scan deletes the client without roles it creates.

| Variable | Default |
|----------|---------|
| `OCTO_CANARY_TENANT` | required |
| `OCTO_CANARY_CLIENT_ID` / `OCTO_CANARY_CLIENT_SECRET` | `OCTO_CLI_CLIENT_ID` / `OCTO_CLI_CLIENT_SECRET` (an administrator client of the tenant) |
| `OCTO_CANARY_IDENTITY_URL` / `OCTO_CANARY_ASSETREPO_URL` | `https://localhost:5003/` / `https://localhost:5001/` |
| `OCTO_CANARY_LOG_DIR` | the first `logFiles` folder above the test assembly |
| `OCTO_CANARY_OTLP_DIR` | none (telemetry not scanned) |
| `OCTO_CANARY_BROKER_HOST` / `_PORT` / `_USER` / `_PASSWORD` / `OCTO_CANARY_INSTANCE_PREFIX` | `localhost` / `5672` / `guest` / `guest` / `default` (the local stack) |
| `OCTO_CANARY_RESULT_DIR` | the test output folder |

A hit becomes a Bug under Feature F3.7 (AB#6884); results are attached to AB#7118.
