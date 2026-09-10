## What does this change?

<!-- Explain why, not just what. -->

## Type of change

- [ ] Desktop application
- [ ] Local service or launcher
- [ ] SIP signaling or media
- [ ] ConnectWise integration
- [ ] Call history, export, or retention
- [ ] Build, tests, or CI
- [ ] Documentation

## Security review

Which of the seven rules in [CONTRIBUTING.md](../CONTRIBUTING.md) does this touch?

<!-- e.g. "Rule 2 - per-session bearer credential", or "none" -->

- [ ] The service still binds to loopback only.
- [ ] All routes except `/health` still require the per-session bearer credential.
- [ ] Browser origins and non-loopback host headers are still rejected.
- [ ] No certificate-bypass path was added for TLS SIP signaling.
- [ ] Desktop secrets remain DPAPI-protected and excluded from serialized settings.
- [ ] Request sizes and rates remain bounded.
- [ ] Logs still omit telephone numbers, call content, contact data, and tokens.
- [ ] Retention and confirmed deletion still remove data as configured.

## Verification

```powershell
dotnet restore .\CallBridge.sln
dotnet build .\CallBridge.sln -c Release --no-restore
dotnet test .\tests\CallBridge.Service.Tests\CallBridge.Service.Tests.csproj -c Release --no-build
dotnet run --project .\src\CallBridge.Desktop\tests\CredentialSmokeTest.csproj -c Release
dotnet run --project .\src\CallBridge.Desktop\tests\ConnectWiseSmoke\ConnectWiseSmoke.csproj -c Release
dotnet run --project .\src\CallBridge.Desktop\tests\SipMediaSmoke\SipMediaSmoke.csproj -c Release
```

- [ ] Release build is warning-free (`TreatWarningsAsErrors` is on)
- [ ] Service tests pass
- [ ] Relevant smoke tests pass
- [ ] Dependency lock files committed if dependencies changed
- [ ] Tests use synthetic `555` numbers and `example.invalid` domains

## Rollback

<!-- How would this be reverted safely? Note anything affecting persistence or credentials. -->
