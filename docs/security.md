# security and privacy

## local boundary

The gateway binds to loopback and requires the configured API key for model routes. There is no remote start/stop endpoint. Requests sent directly to the worker's private port bypass the controller's admission policy; clients should use the gateway address shown in the window.

The application starts only executables and files that you select. Treat a Flare binary, model package, publisher profile, or miner as software you are choosing to trust. Model packages include executable GPU resources. No model converter, miner download, or signature bypass is built in.

Starting workloads requests Windows elevation for the controller, including when the power cap is unchanged; the portable HTTP.sys listener also requires it. The Flare and miner processes inherit the controller's elevated permissions. Select only trusted binaries and packages. Do not substitute an untrusted executable or grant access to an untrusted local configuration. This release is not a security boundary against another process running as your own Windows account or an administrator.

## settings and logs

Local settings and runtime state are stored under `%LOCALAPPDATA%\FlareIdle`, protected with file access controls for the user and system. Settings use Windows DPAPI encryption scoped to the current user. While serving, Flare reads the API key from a temporary access-controlled file; normal shutdown removes that file. This does not protect secrets from another process running as that user or an administrator. Keep backups and support attachments private. API keys must not be committed to Git, placed in screenshots, or posted in issues. A mining wallet address may be public on-chain, but it can still identify you.

There is no application analytics or developer pool configuration. Flare and your miner have their own network behavior and policies; review those separately. The app does not upload model files.

## workload ownership

Workers start suspended and enter owned Windows job objects before execution. Closing the app stops owned processes; an independent watchdog observes controller health. Mining is stopped before inference is admitted, and the idle timer starts after requests drain.

These measures reduce accidental orphan processes. They do not make driver faults, power loss, hardware failure, or hostile native code recoverable. The app does not flash firmware or modify voltage, clocks, or fan curves.

## reporting

Use the repository's private vulnerability reporting feature if available. Otherwise, open an issue describing the affected component without exploitation details or secrets, and ask for a private reporting channel. For ordinary bugs, include app/runtime versions and a minimal reproduction with redacted logs. Do not attach your settings directory or model package.
