# NorthAPI release validation

SDK: .NET 10.0.403 (pinned in global.json). Run from the repository root.

```powershell
dotnet test NorthAPI.slnx --configuration Release --logger "trx;LogFileName=release-final.trx" --results-directory artifacts/qa -p:IntermediateOutputPath=obj/qa10/
dotnet publish northApi.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/win-x64 -p:IntermediateOutputPath=obj/publish-qa/
```

The external tests use https://httpbingo.org documented HTTP method endpoints,
synthetic payloads, and an explicit NorthAPI-QA User-Agent. No real credentials
are sent to the public API. Network failures are failures, not automatic skips.

The code execution tests compile and run generated C# and invoke the real native
Windows curl.exe with the generated arguments. A test-only literal POSIX quote
parser decodes arguments because this PC has no configured POSIX shell. This
is not end-to-end validation in bash/sh. Scenario 5 is an additional Unicode
regression: it fails on this PC's Windows cURL 7.79.1 and is intentionally retained.
Do not remove Unicode or change expected request bytes to make it green.

Process restart tests launch the real NorthAPI application, send and save a
request through UI Automation, close, restart, and inspect collections, history,
and environments. All tests use isolated temporary data. Production local data
is never replaced by test fixtures.

To smoke-test a published executable on the development PC:

```powershell
$env:NORTHAPI_QA_EXE = (Resolve-Path artifacts/win-x64/northApi.exe).Path
dotnet test tests/ReleaseQA/ReleaseQA.csproj --configuration Release --no-build --filter "FullyQualifiedName~ProcessRestartTests" --logger "trx;LogFileName=published-process-smoke.trx" --results-directory artifacts/qa
Remove-Item Env:NORTHAPI_QA_EXE
```

Only the launched child app receives NORTHAPI_DATA_DIRECTORY and empty DOTNET_ROOT
settings. That smoke check does not prove a clean-machine deployment: this PC
still has .NET installed. A separate Windows system without .NET must be used
before approving that requirement. Record OS, runtime absence, artifact hash,
startup, network requests, write permissions, restart persistence, and exit code.

Persistent workspace state and recovery backups use current-user Windows DPAPI.
Explicit exports and generated code retain user-requested values. Test output
uses synthetic canaries and assertions that do not print credential values.
