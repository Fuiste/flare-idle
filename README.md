# flare idle

A small Windows app that keeps a Flare model ready and runs your miner between requests.

Flare gets priority: before a request reaches the model, the app stops its miner. The model stays loaded, including the cache Flare retains. After an idle grace period, mining resumes. The window shows the current state, GPU power and temperature, VRAM, model activity, and mining telemetry.

**This is an early release for Windows and NVIDIA GPUs supported by Flare.** Current upstream Flare targets **SM120/SM121 Blackwell GPUs**. Selecting a smaller model does not make an older GPU compatible. See [compatibility](docs/compatibility.md) before downloading model files.

## get started

1. Download the Windows x64 ZIP from this repository's **Releases**, extract it, and open `FlareIdle.exe`.
2. Obtain a trusted native Windows `flare.exe` and a compatible **prepared Flare runtime package** separately. Keep the package's sibling payload files beside its main `.safetensors` file. This project does not distribute Flare, miners, or model weights.
3. Complete Flare's first-use publisher verification in its own terminal, then stop that worker. Use the same trust-store file in this app. A publisher file alone does not record acceptance; see [first-use trust](docs/compatibility.md#first-use-trust).
4. Open **settings**. Select the Flare executable, model package, GPU, model alias, and trust files. Use **generate** to create a local API key or enter your own.
5. To enable mining, obtain [Krig from its official releases](https://github.com/kryptex/krig-miner/releases), select its executable, and enter your coin, pool address or addresses, the pool's user value—usually your wallet or `wallet.worker`—and an optional pool password. Use the values documented by your pool; no account, wallet, or pool is preconfigured.
6. Leave the power limit at **0 / unchanged**, or choose a limit your GPU supports. Start with one model slot and enough reserved VRAM for the miner. Save, then select **start**.

Point an OpenAI-compatible client at the loopback endpoint displayed in the window, using the API key and model alias you configured. The gateway is local-only in this release. Do not route clients directly to Flare's private backend: those requests bypass mining admission.

The app does not start workloads at login or after a restart. **start** requests Windows elevation for the controller; the Flare and miner processes it launches inherit those permissions, so select only trusted binaries. **pause mining** keeps the model available. **stop & close** stops owned workers and restores the power cap captured before this session if the app changed it. Minimizing keeps it running.

## choose a model for your GPU

Use a prepared package for a model and GPU architecture that upstream Flare supports. An ordinary Hugging Face checkpoint, GGUF file, or arbitrarily renamed `.safetensors` file is not a Flare runtime package.

Start small. Model weights, execution buffers, live context/cache, and the miner all need VRAM. Lower concurrency and reserve more memory if the miner cannot initialize. A model's advertised context length is not a promise that every context/concurrency combination fits.

Switch models by stopping the app, changing the model settings, and starting again. The app does not convert checkpoints, download model weights, prepare packages, or move models between CPU and GPU.

Upstream references: [Flare](https://github.com/NVIDIA/flare-local-inference), [model preparation](https://github.com/NVIDIA/flare-local-inference/blob/main/docs/model-preparation.md), and [serving CLI](https://github.com/NVIDIA/flare-local-inference/blob/main/docs/cli-design.md).

## what to expect

- **Agent workloads:** the default two-second grace period avoids starting a miner during every brief tool call. Short gaps may produce little mining; longer gaps are more useful.
- **Request latency:** stopping a GPU process takes time. The app avoids reloading the model, but does not promise an invisible transition or a fixed latency budget.
- **Mining:** this release manages a separate Krig process. It does not share CUDA streams, preempt a running kernel directly, or mine concurrently with active model requests.
- **Power:** an optional cap is checked against the selected GPU's reported limits. The app does not tune voltage, clocks, fan curves, firmware, or drivers. Some cards/drivers do not expose power control; leave the cap unchanged on those systems.
- **Recovery:** owned child processes use Windows job objects. A separate watchdog handles controller failure. Unsupported sensors are shown as unavailable.

Only the selected GPU and one native Flare worker are managed. Multi-GPU tensor-parallel packages, non-Flare inference engines, and arbitrary miner telemetry formats are outside this first release.

## privacy

Settings and runtime state stay under `%LOCALAPPDATA%\FlareIdle`. Settings are protected for the current Windows user with DPAPI and restricted file access. They are excluded from source and release packages. The app references your model and miner files in place; it does not upload them. There is no built-in analytics, pool account, donation address, or remote control service.

Read [security and privacy](docs/security.md) before reporting a problem. Do not attach your configuration, API key, wallet, or raw logs to a public issue.

## build

On Windows x64, with .NET Framework 4.8 available:

```powershell
.\build.ps1
.\tests\run.ps1
.\scripts\release.ps1 -Version 0.1.0-preview.1 -SkipBuild -SkipTests
```

The build uses Windows' Framework C# compiler and WPF. The normal test command uses CPU-only fixtures; it does not start a miner or model or change GPU settings. It needs a normal Windows process because restricted sandboxes can block the loopback HTTP test server.

See [development and releases](docs/development.md), [troubleshooting](docs/troubleshooting.md), and [third-party notices](THIRD_PARTY_NOTICES.md).

This project is independent of NVIDIA and the authors of Flare or Krig.
