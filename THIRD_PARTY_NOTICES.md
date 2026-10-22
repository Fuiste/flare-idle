# third-party notices

The release archive contains the flare idle application and its documentation. It does **not** bundle an inference engine, miner, NVIDIA libraries, model weights, or prepared model packages.

- [NVIDIA Flare](https://github.com/NVIDIA/flare-local-inference) is a separate project licensed under [Apache-2.0](https://github.com/NVIDIA/flare-local-inference/blob/main/LICENSE). Users obtain their own compatible runtime and package. This project does not change Flare's license or package-signing requirements.
- [Krig](https://github.com/kryptex/krig-miner) is a separately obtained miner. Its distribution, license, developer fees, supported coins, and pool requirements remain the miner author's responsibility. No Krig binary is redistributed here.
- NVIDIA NVML is provided by the user's NVIDIA driver. The app calls its public native interface; no NVML DLL is included.
- Windows and .NET Framework/WPF are platform dependencies. Their licenses remain with their respective owners.

The Windows child-process/job-object utility was adapted from the application's original local launcher code. The conventional Windows/NVML interop declarations do not incorporate a bundled third-party runtime.

Product and project names belong to their owners. No affiliation or endorsement is implied.
