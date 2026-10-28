# NorthAPI V1 — Release QA

Assessment: **2026-10-28**. Recommendation: **BLOCKED**.

Local execution access is restored. This report supersedes the earlier access-denied
assessment. Current source was inspected before changes. No UI layout was redesigned.

Raw run artifacts are generated locally and excluded from source control. The recorded outcomes and reproduction commands below remain part of the public report.

## Executed results

- `dotnet test NorthAPI.slnx --configuration Release ...`: **61 executed, 60 Pass, 1 Fail, 0 skipped**.
  Evidence: `artifacts/qa/release-final.trx`. Exit code **1**.
- Self-contained `win-x64` Release publish: **Pass**, exit code **0**.
- Published executable smoke test on the development PC: **1 Pass**.
  Evidence: `artifacts/qa/published-process-smoke.trx`.
- Existing regression harnesses: **62 HTTP/workspace checks** and **16 WPF workflow checks**
  executed successfully after the changes. These are separate console checks, not
  additional discovered xUnit tests. Evidence: `artifacts/qa/legacy-http.log`,
  `artifacts/qa/legacy-wpf.log`.
- A separate clean Windows environment without .NET: **Blocked — unavailable**.

The test host was Windows 11 Pro, version 10.0.22000, with SDK **10.0.403**.
The published bundle contains .NETCore and WindowsDesktop **10.0.14**.
PE machine verification returned **0x8664 (x64)**. Artifact metadata and executable
SHA-256 are recorded in `artifacts/qa/release-artifact.json`.
This describes the tested host, not a claim about a supported OS matrix.

## Phase 1 — HTTP integration

**Pass:** GET, POST, PUT, PATCH, DELETE against the deterministic loopback server
and the external HTTPS API at `https://httpbingo.org/{get,post,put,patch,delete}`.
The external tests use synthetic non-secret payloads and an explicit
`NorthAPI-QA/1.0` User-Agent. Tests assert status, response headers, echoed method,
query parameters, custom headers, body, elapsed time, and decoded body byte size.
They use NorthAPI's request builder and HTTP engine, not an unrelated web client.
The endpoint contract is documented by [httpbingo](https://httpbingo.org/).

**Pass:** 400, 401, 404, 429, 500, 503 return inspectable HTTP results with status,
headers, and JSON error bodies. WPF tests also render these results and their
metadata; no `EnsureSuccessStatusCode` converts them into transport failures.

Earlier external attempts against `/anything/northapi-qa` without an explicit
User-Agent returned **402 for all five methods**. Those failures are preserved in
`artifacts/qa/external-https.trx`. The test setup was changed to
published method endpoints with a QA User-Agent; all subsequent external cases
passed, including the final complete run. Route and User-Agent were changed
 together, so the exact cause of the earlier 402s was not isolated. The application
was not changed to hide 402s. The successful setup is independently recorded in
`artifacts/qa/external-https-identified.trx`.

## Phase 2 — Failure handling

**Pass:** invalid URLs (four forms), reserved `.invalid` DNS lookup, closed local
port, untrusted self-signed local TLS certificate, cancellation, timeout while
waiting for headers, timeout while reading the body, malformed JSON, and empty
204 responses. DNS is tested through a normal HttpClient with proxy use disabled
to isolate DNS resolution. The default application transport is not altered.
The TLS test does not install a trust root or bypass certificate validation.

**Pass:** dispatcher timers continue ticking while an actual request editor is
waiting for cancellation or timeout. The Send button returns to its normal state,
errors display safely, and failed requests are recorded in protected history.
Malformed JSON falls back to raw display; empty bodies render an empty-body message.

Error messages now describe URL/settings, DNS, connection, TLS, cancellation,
timeout, or file-access categories without inserting raw exception text. This
avoids accidentally persisting a credential included in an exception message.

## Phase 3 — Authentication and security

### Confirmed findings and fixes

1. **High: plaintext persistent secrets — fixed.** The inspected store directly
   serialized auth, headers, bodies, URLs, history, and environment values to JSON.
   Entire workspace payloads now use Windows DPAPI **CurrentUser**, including
   recovery backups. This covers credentials embedded outside explicit auth fields.
   Valid legacy files and old application recovery backups migrate automatically.
   Corrupt or undecryptable files are preserved and saving is disabled rather than
   silently overwriting their contents. Temporary save files contain protected data.
   Actual encrypt/decrypt, migration, canary scans, corruption, read-only-file,
   missing-store, and ciphertext-tamper tests passed. DPAPI's user-bound protection
   is documented by [Microsoft](https://learn.microsoft.com/dotnet/standard/security/how-to-use-data-protection).
   Cross-account decryption rejection was **Not Run**: no second Windows identity
   was available. Protection from a malicious process running as the same user is
   not claimed.
2. **Medium: raw exception disclosure — fixed.** UI errors, persisted history errors,
   load warnings, and local-file error dialogs no longer insert raw exception messages.
   `SafeDiagnostics` returns fixed category messages. Source inspection found no
   application file logger, console logger, Trace logger, or Debug logger.
   Dynamic tests captured Console and Trace during store, export, code generation,
   and diagnostic operations: no output or canary was emitted. Tests never print
   credential values on assertion failure. This is not an audit of OS crash dumps,
   arbitrary external tracing, or a debugger's memory access.
3. **Medium: API-key header injection / silent auth fallback — fixed.** CR/LF in
   API-key header names/values is rejected. Unknown auth types, methods, and body
   modes are rejected at import/persistence boundaries instead of reopening with
   a different method or missing auth. Regression assertions passed.
4. **Release stability: preview framework and missing icon — fixed.** The inspected
   project targeted `net11.0-windows` and referenced a missing `assets/app.ico`.
   App and tests now target .NET 10; `global.json` pins installed stable SDK 10.0.403.
   The same compass icon was restored from its existing SVG, with reproducible
   generation in `scripts/Generate-AppIcon.ps1`. No visual redesign was performed.

**Pass:** synthetic Basic and Bearer values reach the local server correctly.
Credential comparisons use boolean assertions that withhold values.

**Pass:** explicit collection exports retain the requested values; they were not
silently redacted or encrypted. Generated examples also retain explicitly requested
authentication. These are deliberate user outputs, separate from protected internal
storage and diagnostics. A generated example prints its HTTP response body by design;
an echo API may return request credentials in that body. No real credentials were used.

Local DPAPI protection is scoped to the current Windows account. Move collections
to another account/device through an explicit export/import, not by assuming the
protected workspace file is portable. Existing unrelated user-created exports are
not modified by migration.

## Phase 4 — Generated code execution

**Pass:** generated C# examples were compiled and actually executed against the
local server. GET, POST JSON, custom headers (including quotes), Basic, and Bearer
cases matched NorthAPI's observed method, URL, application headers, and body.
The C# substep also executed and matched in the additional Unicode scenario.

**Pass with a defined scope:** the real native `curl.exe` executed the generated
GET, POST JSON, custom-header, Basic, and Bearer argument streams and matched the
same wire data. A test-only POSIX literal quote decoder converts the existing
POSIX generator's command into arguments; it does not substitute an HTTP client.

**Fail:** scenario 5, Unicode raw JSON, differs when run by this PC's native
Windows cURL **7.79.1**. The C# substep matches, then the cURL body comparison fails
with `Generated body differs; values withheld.` The fixture body is non-secret
`{"text":"café"}`. Root cause across Windows argv/code-page handling versus the
intended POSIX shell environment has not been fully isolated. This failure is not
suppressed, skipped, or counted as passed. Evidence:
`artifacts/qa/release-final.trx`,
`artifacts/qa/wpf-code-execution.trx`.

**Blocked:** end-to-end copy/paste execution in a POSIX shell. At the time of that QA run, no bash/sh or
configured WSL distribution was available. Native argument-stream execution
is not represented as a successful bash/sh test.

Reproduce the code-generation regression:

```powershell
dotnet test tests/ReleaseQA/ReleaseQA.csproj --configuration Release --filter "FullyQualifiedName~CodeExecutionTests" -p:IntermediateOutputPath=obj/qa10/
```

Inspect `scenario: 5`; the other five executed request scenarios pass. Resolve
byte-preserving execution in the supported shell/cURL environment or provide an
explicitly documented Windows-compatible UTF-8 export path. Do not make the test
pass by stripping Unicode, changing the expected body, or silently changing an
explicit export's values.

## Phase 5 — Persistence

**Pass:** the real application process is launched with an isolated
`NORTHAPI_DATA_DIRECTORY`. UI Automation creates a request, sends it, saves it
through the Save dialog, closes the app, and starts a new process. The new process
shows the saved collection, history, and environment. Exit codes are zero.
The environment is initially seeded through the production store; the separate
16-check WPF regression harness also executes the environment-save dialog.

**Pass:** protected collection/history/environment round trips, legacy migration,
missing storage initialization, read-only write rejection without data loss,
protected corrupt-file recovery, and ciphertext-tamper rejection. Corruption
backups retain encrypted recovery material and the store remains read-only until
recovery, rather than resetting and accepting writes over the old data.

The first process-restart test failed because the test searched only desktop
children for an owned modal window. The test was corrected to search descendants
for a Window belonging to the app process. No application behavior was changed
for that test failure. Evidence: `artifacts/qa/process-restart.trx`,
`artifacts/qa/process-restart-fixed.trx`, and final suite.

## Phase 6 — Release deployment

**Pass:** the following command executed with exit code 0:

```powershell
dotnet publish northApi.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/win-x64 -p:IntermediateOutputPath=obj/publish-qa/
```

The `artifacts/win-x64/` contains the apphost, coreclr,
hostfxr, and both included runtimes. The PE architecture and artifact hash were
checked. See `artifacts/qa/release-artifact.json`.

**Pass on development PC only:** `NORTHAPI_QA_EXE` selects the actual published
executable for the restart test. The child receives empty DOTNET_ROOT and
DOTNET_ROOT_X64 directories and DOTNET_MULTILEVEL_LOOKUP=0. Published startup,
HTTP, protected local saving, close/restart, and shutdown passed.
Evidence: `artifacts/qa/published-process-smoke.trx`.
Local write-denial behavior is separately tested with a read-only store file.

**Blocked:** a genuinely separate Windows VM/PC with no modern .NET runtime
preinstalled. The development machine still has SDKs/runtimes and registry entries.
Empty runtime-root settings and a successful self-contained publish are not proof
of a clean-machine deployment. Clean-machine startup, network, standard-user
storage permissions, and shutdown remain **Not Run** until that environment exists.

## Reproduction commands

See [the QA README](tests/ReleaseQA/README.md) for fixture isolation and smoke-test setup.

```powershell
dotnet test NorthAPI.slnx --configuration Release --logger "trx;LogFileName=release-final.trx" --results-directory artifacts/qa -p:IntermediateOutputPath=obj/qa10/
dotnet run --project tests/HttpEngineChecks/HttpEngineChecks.csproj --configuration Release
dotnet run --project tests/WorkspaceUiChecks/WorkspaceUiChecks.csproj --configuration Release -p:OutputPath=bin/qa10-ui/ -p:IntermediateOutputPath=obj/qa10-ui/
```

The first command intentionally exits nonzero while the Unicode/native-cURL
regression remains. No test is changed to a pass based on source inspection alone.
Test fixtures are synthetic; actual user credential stores are not printed or
replaced by fixtures.

## Remaining release blockers

1. Resolve the Unicode/native-Windows-cURL wire mismatch and execute the POSIX
   generator in its documented shell environment.
2. Test the published x64 artifact on a separate Windows system without .NET,
   recording artifact hash, OS/architecture, startup, HTTP requests, local storage
   permissions, persistence, and clean shutdown.

Final recommendation: **BLOCKED**. Most functional/security checks now pass, but
one executed test still fails and the required clean-machine check cannot be claimed.

## Individual executed test results

All rows below come from `artifacts/qa/release-final.trx`. Duration and outcome are
actual runner evidence. Published-app and legacy-harness results are reported
separately above.

| Test name | Status | Evidence |
| --- | --- | --- |
| ReleaseQA.CodeExecutionTests.Generated_CSharp_and_native_curl_execute_and_match_original_requests(scenario: 0) | Pass | release-final.trx; 00:00:01.9520757 |
| ReleaseQA.CodeExecutionTests.Generated_CSharp_and_native_curl_execute_and_match_original_requests(scenario: 1) | Pass | release-final.trx; 00:00:01.7449747 |
| ReleaseQA.CodeExecutionTests.Generated_CSharp_and_native_curl_execute_and_match_original_requests(scenario: 2) | Pass | release-final.trx; 00:00:01.7497112 |
| ReleaseQA.CodeExecutionTests.Generated_CSharp_and_native_curl_execute_and_match_original_requests(scenario: 3) | Pass | release-final.trx; 00:00:01.7429018 |
| ReleaseQA.CodeExecutionTests.Generated_CSharp_and_native_curl_execute_and_match_original_requests(scenario: 4) | Pass | release-final.trx; 00:00:02.0606431 |
| ReleaseQA.CodeExecutionTests.Generated_CSharp_and_native_curl_execute_and_match_original_requests(scenario: 5) | Fail | release-final.trx; 00:00:01.9839328 |
| ReleaseQA.CodeExecutionTests.POSIX_literal_quote_decoding_matches_generator(command: "curl 'a b'", expected: "a b") | Pass | release-final.trx; 00:00:00.0001564 |
| ReleaseQA.CodeExecutionTests.POSIX_literal_quote_decoding_matches_generator(command: "curl 'a'\"'\"'b'", expected: "a'b") | Pass | release-final.trx; 00:00:00.0000032 |
| ReleaseQA.CodeExecutionTests.POSIX_literal_quote_decoding_matches_generator(command: "curl 'literal $x `y` \\ path'", expected: "literal $x `y` \\ path") | Pass | release-final.trx; 00:00:00.0005213 |
| ReleaseQA.HttpTests.Cancellation_is_distinct_from_timeout | Pass | release-final.trx; 00:00:00.1152622 |
| ReleaseQA.HttpTests.DNS_failure_is_handled | Pass | release-final.trx; 00:00:00.0127889 |
| ReleaseQA.HttpTests.External_HTTPS_httpbingo(method: "DELETE") | Pass | release-final.trx; 00:00:00.1652218 |
| ReleaseQA.HttpTests.External_HTTPS_httpbingo(method: "GET") | Pass | release-final.trx; 00:00:00.2807251 |
| ReleaseQA.HttpTests.External_HTTPS_httpbingo(method: "PATCH") | Pass | release-final.trx; 00:00:00.1686233 |
| ReleaseQA.HttpTests.External_HTTPS_httpbingo(method: "POST") | Pass | release-final.trx; 00:00:00.2320634 |
| ReleaseQA.HttpTests.External_HTTPS_httpbingo(method: "PUT") | Pass | release-final.trx; 00:00:00.1561236 |
| ReleaseQA.HttpTests.Invalid_URL_is_understandable(url: "bad url") | Pass | release-final.trx; 00:00:00.0000967 |
| ReleaseQA.HttpTests.Invalid_URL_is_understandable(url: "file:///C:/Windows") | Pass | release-final.trx; 00:00:00.0008182 |
| ReleaseQA.HttpTests.Invalid_URL_is_understandable(url: "http://[invalid") | Pass | release-final.trx; 00:00:00.0001180 |
| ReleaseQA.HttpTests.Invalid_URL_is_understandable(url: "https://") | Pass | release-final.trx; 00:00:00.0007889 |
| ReleaseQA.HttpTests.Local_methods_status_headers_body_time_size(method: "DELETE") | Pass | release-final.trx; 00:00:00.0075892 |
| ReleaseQA.HttpTests.Local_methods_status_headers_body_time_size(method: "GET") | Pass | release-final.trx; 00:00:00.0019941 |
| ReleaseQA.HttpTests.Local_methods_status_headers_body_time_size(method: "PATCH") | Pass | release-final.trx; 00:00:00.0019633 |
| ReleaseQA.HttpTests.Local_methods_status_headers_body_time_size(method: "POST") | Pass | release-final.trx; 00:00:00.0024296 |
| ReleaseQA.HttpTests.Local_methods_status_headers_body_time_size(method: "PUT") | Pass | release-final.trx; 00:00:00.0027490 |
| ReleaseQA.HttpTests.Malformed_and_empty_are_HTTP_results(path: "/empty", status: 204) | Pass | release-final.trx; 00:00:00.0029460 |
| ReleaseQA.HttpTests.Malformed_and_empty_are_HTTP_results(path: "/malformed", status: 200) | Pass | release-final.trx; 00:00:00.0017989 |
| ReleaseQA.HttpTests.Non_success_responses_remain_inspectable(status: 400) | Pass | release-final.trx; 00:00:00.0078334 |
| ReleaseQA.HttpTests.Non_success_responses_remain_inspectable(status: 401) | Pass | release-final.trx; 00:00:00.0067675 |
| ReleaseQA.HttpTests.Non_success_responses_remain_inspectable(status: 404) | Pass | release-final.trx; 00:00:00.0015017 |
| ReleaseQA.HttpTests.Non_success_responses_remain_inspectable(status: 429) | Pass | release-final.trx; 00:00:00.0013710 |
| ReleaseQA.HttpTests.Non_success_responses_remain_inspectable(status: 500) | Pass | release-final.trx; 00:00:00.0022791 |
| ReleaseQA.HttpTests.Non_success_responses_remain_inspectable(status: 503) | Pass | release-final.trx; 00:00:00.0013473 |
| ReleaseQA.HttpTests.Refused_connection_is_handled | Pass | release-final.trx; 00:00:02.0541705 |
| ReleaseQA.HttpTests.Timeout_includes_headers_and_body(path: "/slow") | Pass | release-final.trx; 00:00:00.2167460 |
| ReleaseQA.HttpTests.Timeout_includes_headers_and_body(path: "/slow-body") | Pass | release-final.trx; 00:00:00.2186436 |
| ReleaseQA.HttpTests.Untrusted_TLS_is_rejected | Pass | release-final.trx; 00:00:00.1230926 |
| ReleaseQA.ProcessRestartTests.Application_saves_sends_closes_and_reopens_collections_history_environments | Pass | release-final.trx; 00:00:06.2807076 |
| ReleaseQA.SecurityStorageTests.API_key_header_linebreaks_and_unsupported_auth_are_rejected | Pass | release-final.trx; 00:00:00.0022134 |
| ReleaseQA.SecurityStorageTests.Auth_reaches_server_but_is_not_logged(type: "basic") | Pass | release-final.trx; 00:00:00.0096065 |
| ReleaseQA.SecurityStorageTests.Auth_reaches_server_but_is_not_logged(type: "bearer") | Pass | release-final.trx; 00:00:00.0626829 |
| ReleaseQA.SecurityStorageTests.Corrupt_file_is_preserved_protected_and_readonly_not_overwritten | Pass | release-final.trx; 00:00:00.0257535 |
| ReleaseQA.SecurityStorageTests.Diagnostics_never_echo_request_values_or_exception_text | Pass | release-final.trx; 00:00:00.0160125 |
| ReleaseQA.SecurityStorageTests.DPAPI_protects_collections_history_environment_and_preserves_explicit_exports | Pass | release-final.trx; 00:00:00.1419057 |
| ReleaseQA.SecurityStorageTests.Environment_templates_resolve_and_reject_cycles_and_missing_keys | Pass | release-final.trx; 00:00:00.0066536 |
| ReleaseQA.SecurityStorageTests.Legacy_plaintext_and_old_backups_migrate_without_leaving_plaintext | Pass | release-final.trx; 00:00:00.0323337 |
| ReleaseQA.SecurityStorageTests.Missing_store_initializes_and_readonly_write_fails_without_data_loss | Pass | release-final.trx; 00:00:00.0178255 |
| ReleaseQA.SecurityStorageTests.Persistence_and_code_generation_emit_no_secret_to_console_or_trace | Pass | release-final.trx; 00:00:00.0169689 |
| ReleaseQA.SecurityStorageTests.Tampered_DPAPI_ciphertext_is_rejected_without_discarding_original | Pass | release-final.trx; 00:00:00.0229939 |
| ReleaseQA.WpfTests.Invalid_URL_shows_safe_UI_error | Pass | release-final.trx; 00:00:00.0446214 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 200) | Pass | release-final.trx; 00:00:00.0092575 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 400) | Pass | release-final.trx; 00:00:00.0113696 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 401) | Pass | release-final.trx; 00:00:00.0091343 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 404) | Pass | release-final.trx; 00:00:00.0108234 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 429) | Pass | release-final.trx; 00:00:00.0173145 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 500) | Pass | release-final.trx; 00:00:00.2885387 |
| ReleaseQA.WpfTests.Response_viewer_displays_HTTP_errors_and_metadata(status: 503) | Pass | release-final.trx; 00:00:00.0098492 |
| ReleaseQA.WpfTests.UI_remains_responsive_during_cancellation_and_timeout(cancel: False) | Pass | release-final.trx; 00:00:01.0913982 |
| ReleaseQA.WpfTests.UI_remains_responsive_during_cancellation_and_timeout(cancel: True) | Pass | release-final.trx; 00:00:00.2333244 |
| ReleaseQA.WpfTests.Viewer_handles_malformed_and_empty_JSON(path: "/empty") | Pass | release-final.trx; 00:00:00.0113376 |
| ReleaseQA.WpfTests.Viewer_handles_malformed_and_empty_JSON(path: "/malformed") | Pass | release-final.trx; 00:00:00.0097237 |
