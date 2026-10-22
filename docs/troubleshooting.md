# troubleshooting

## Windows reports an unknown publisher

Early release binaries may be unsigned. The SHA-256 sidecar detects a changed or incomplete download; it does not establish publisher trust. Run a release only if you trust its source, or build the application from the repository yourself.

## the model will not load

Check that the selected file is a prepared Flare runtime package, that all sibling payload files are present, and that your runtime, GPU architecture, publisher profile, and trust settings match it. Complete [first-use trust](compatibility.md#first-use-trust) in Flare's terminal first and select that same trust-store file. A generic checkpoint or GGUF file cannot be substituted. Review Flare's error before changing memory settings.

Use the smallest compatible package first. Reduce concurrency and leave memory for execution buffers and the miner. The advertised maximum context is not the same as a configuration tested on your GPU.

## power control is unavailable

Set the power limit to zero to leave it unchanged. Some NVIDIA devices or drivers do not expose adjustable limits. An explicit limit must fit the range reported by the selected GPU. Do not use firmware or overclocking tools to make an unsupported setting appear supported.

## mining never starts

Check that mining is enabled, the app is not paused, no request is active or queued, the idle grace period has elapsed, and native Flare telemetry is healthy. Confirm the miner binary, coin, pool URL, and user value using the pool's documentation. Initialization can consume most of a short idle gap.

## mining stops whenever an agent calls the model

That is the intended priority policy. The app stops the miner before forwarding a request and keeps it stopped while streams or queued work remain. Increasing the idle grace period can avoid unproductive restarts during rapid tool use.

## first tokens are slower after mining

Process teardown, driver scheduling, and the next inference step all take time. Keeping weights resident avoids a model reload but does not eliminate handoff latency. Compare like-for-like prompts and warm-cache state; do not treat one sample as a latency guarantee.

## an API client cannot connect

Use the loopback gateway address and model alias shown by this app, with its configured API key. The gateway must be ready before it serves model requests. It does not expose a LAN listener in this release. Check for another process using the chosen port.

## the app reports stale telemetry or recovery required

Do not assume a zero reading means idle. Leave the app's fault visible, stop new work, and inspect its status. After normal shutdown, verify that owned workers are gone before starting a different GPU controller. Avoid running another miner or power-control utility against the same GPU at the same time.

The app blocks another session while an earlier session still needs recovery. Usually its guardian finishes automatically. If it cannot, close other GPU workers and run `FlareIdle.exe --recover` from the extracted app directory, approve Windows elevation, and inspect the result before starting again. Recovery uses the saved device identity and pre-session cap; it does not reset clocks or firmware.
