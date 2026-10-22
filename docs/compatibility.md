# compatibility

## platform

The app is Windows x64 software using .NET Framework 4.8/WPF, Windows job objects, and NVIDIA NVML. It is not a Linux, WSL, macOS, AMD, or Intel GPU controller.

GPU detection alone is not an inference compatibility test. As checked on 22 October 2026, upstream Flare targets NVIDIA **SM120 and SM121**. Its [README](https://github.com/NVIDIA/flare-local-inference) describes it as a research prototype with deliberately limited model and hardware coverage. Consumer Blackwell GPUs can use appropriately sized packages; a workstation-class card is not a requirement. Older NVIDIA GPUs are not made compatible by choosing a smaller checkpoint.

Flare's support evolves independently. Check the [current model-preparation guide](https://github.com/NVIDIA/flare-local-inference/blob/main/docs/model-preparation.md) and the runtime you actually installed before choosing a GPU/package pair.

## prepared packages

Flare serves prepared runtime packages, not arbitrary checkpoint files. A current package has a main `.safetensors` file and may have content-addressed sibling payload blobs containing weights, kernels, graphs, and supporting resources. Preserve all required siblings when moving a package. Older package formats can differ.

The package determines which model implementation, GPU architecture, and prepared execution capacities are available. Publisher profiles, signature trust, and any first-use acceptance remain part of Flare's security model. Use trusted packages and the matching publisher/trust files; flare idle does not bypass signature checks.

This first release selects one GPU and starts one local Flare worker. A package prepared for multiple tensor-parallel ranks cannot be treated as a one-GPU package. Flare supports broader arrangements, but this controller does not expose them yet.

The Flare child uses the selected CUDA device UUID; Krig is selected by its reported PCI address. A nonzero PCI domain is not supported by this first mining adapter. Multiple physical GPUs and mixed-adapter configurations have not been validated on hardware.

## first-use trust

Flare requires explicit first-use acceptance of the signing key and publisher namespace. Providing `publisher.json` verifies a signature; it does not silently accept the publisher or model license. The controller does not enter an approval for you.

Before starting the package through this app, run it once in Flare's own terminal using an explicit trust-store file. A typical command, from a folder containing your package and trust files, is:

```text
flare run model.safetensors --gpus 0 --publisher-keys publisher.json --trust-store trust.toml
```

Use the GPU index and publisher profile appropriate to your package. Read Flare's disclosure and approve only a source you trust. Stop that Flare process before starting this app, then select the same `trust.toml` in settings. Publisher profiles can be omitted when the package does not need an explicit one. Follow the current [signing and trust instructions](https://github.com/NVIDIA/flare-local-inference/blob/main/docs/cli-design.md#signing-and-local-development) if your runtime differs.

## memory and throughput

Choose a model that leaves room for its execution buffers, contexts, cache, and your miner. Start with concurrency one and a short context, then increase only after measuring the actual combination. Reserved VRAM is headroom for other allocations, not a guarantee that a particular miner will fit. The app does not evict a loaded model to make room for mining.

Keeping the worker alive preserves the state Flare itself retains. Cache capacity and eviction still belong to Flare. Changing the selected package requires stopping and restarting the worker.

## mining and power

The initial miner adapter is [Krig](https://github.com/kryptex/krig-miner). Obtain a trusted compatible binary separately and use a supported coin/pool. The app supplies the configured pool and user value; it does not create a wallet or pool account. Miner telemetry availability depends on the binary's API.

Power control depends on the selected device, driver, and permissions. A limit of zero means leave the cap unchanged. For an explicit limit, the app validates the reported supported range and restores the pre-session cap on shutdown. It does not assume a particular GPU's factory wattage. Unsupported or unreadable sensor values remain unavailable.

## evidence limits

CPU tests exercise scheduling, proxy behavior, configuration, and lifecycle fixtures. They cannot establish safe thermals, model correctness, long-context capacity, or mining profitability on an arbitrary machine. Test your chosen runtime/package/miner combination with conservative settings and inspect the live status.
