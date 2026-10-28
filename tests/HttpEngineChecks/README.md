# HTTP and workspace checks

Run from the repository root:

```powershell
dotnet run --project tests/HttpEngineChecks/HttpEngineChecks.csproj
```

The executable uses real HTTP on an automatically assigned loopback port and
separate temporary workspace files. No external APIs or test packages are needed.

Coverage includes all seven HTTP methods, parameters, custom headers, Basic and
Bearer auth, JSON for POST/PUT/PATCH, multipart/form/binary bodies, redirects,
compression, charsets, byte sizes, response cookies and headers, bounded previews,
timeouts, cancellation, collection save/reopen/update/move/import/export,
history persistence, environment variables, missing/cyclic variables, and code
generation. It prints the location of a generated C# snippet compilation project.

WPF workflow checks exercise the actual Send button, request editor restoration,
response display, local history, sidebar reopening, and environment selection:

```powershell
dotnet run --project tests/WorkspaceUiChecks/WorkspaceUiChecks.csproj -p:OutputPath=bin/feature-ui/ -p:IntermediateOutputPath=obj/feature-ui/
```
