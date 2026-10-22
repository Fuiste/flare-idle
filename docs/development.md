# development and releases

## build and CPU tests

Use a normal Windows x64 session with .NET Framework 4.8 installed. No model, miner, or GPU access is required to build.

```powershell
.\tests\public-scan.ps1
.\build.ps1
.\tests\run.ps1
```

The CPU test suite must not start a real miner, load a model, or change device settings. Loopback gateway fixtures require HTTP.sys support; an execution sandbox that blocks it is not representative of a normal Windows process.

Real-device testing is separate and explicit. Record the GPU/runtime/package/miner versions, configured limit, memory headroom, observed temperature, request/handoff behavior, and shutdown restoration. Use generic or redacted evidence in public issues. Do not copy a developer's runtime settings or test logs into the repository.

## package

```powershell
.\scripts\release.ps1 -Version 0.1.0-preview.1
```

The script builds, runs CPU tests, scans the source, and creates a ZIP plus SHA-256 sidecar under `dist/`. `-SkipBuild -SkipTests` can package an already verified build. The package allowlist contains only `FlareIdle.exe`, this project's license/notices, and user documentation. It does not collect a whole working directory.

Inspect the archive before publishing:

```powershell
.\tests\public-scan.ps1 -Archive .\dist\flare-idle-0.1.0-preview.1-windows-x64.zip
```

Publish the ZIP and `.sha256` file as a GitHub prerelease. The Actions workflow builds and tests in a fresh Windows runner and uploads only those allowlisted release artifacts. A successful CPU workflow is not a GPU-compatibility certification.

## repository hygiene

- Keep user settings, keys, runtime state, logs, local measurements, and screenshots out of Git.
- Do not add model files, Flare binaries, miners, NVIDIA DLLs, or third-party installers to a release.
- Review all tracked content and the archive before the first public push and every release.
- Keep the app's single-GPU ownership and inference-before-mining invariant covered by tests.
- Never turn a missing or stale reading into an asserted zero.

The scanner is a guardrail, not proof that arbitrary content is safe to publish. Human review of the final tracked diff and archive is still required.
