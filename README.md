# LlrpReaderPlatform

<p align="center">
  <img src="src/LlrpReaderPlatform.App.Wpf/Assets/LlrpReader_Pro_Icon.png" alt="LlrpReaderPlatform" width="144" />
</p>

<p align="center">
  <strong>Operator applications and reusable services for LLRP RFID readers</strong>
</p>

<p align="center">
  Version 2.0.3 · .NET 10 · <a href="README.zh-CN.md">中文</a>
</p>

LlrpReaderPlatform is an application platform built on the [LLRPCSharp](https://github.com/newton5555/LLRPCSharp) SDK. It provides reusable reader-management services, persistence, vendor modules, and several UI consumers for discovering readers, editing capability-driven settings, running inventory, accessing tag memory, controlling GPIO, and reviewing inventory history.

The primary field client is a Windows WPF application. A MAUI Blazor Hybrid client reuses the same platform services on Windows, Android, and Mac Catalyst, with a separate experimental Linux GTK4 head. The repository also includes an independent WPF manager for the protocol-level virtual readers supplied by LLRPCSharp.

This is an application repository, not a second LLRP SDK. Protocol encoding, transport, managed reader behavior, and the TCP virtual-device runtime come from LLRPCSharp packages or optional local source references.

## Applications in this repository

| Application | Purpose | Current position |
|---|---|---|
| **LlrpReaderPlatform.App.Wpf** | Full desktop operator client for physical or TCP virtual readers | Primary client and current real-device acceptance surface |
| **LlrpReaderManager** | Responsive MAUI Blazor Hybrid reader client | Shared-services consumer for Windows, Android, and Mac Catalyst |
| **LlrpReaderManager.Linux** | GTK4 head that hosts the same Blazor pages and platform services | Experimental Linux x64 path; built in CI and released as a framework-dependent Debian package |
| **LlrpVirtualDevice.App.Wpf** | Creates and manages TCP/LLRP virtual-reader instances | Independent auxiliary tool; it does not share the physical-reader session manager |

The frozen legacy **LlrpReaderStudio** repository is migration reference only. It is not a runtime dependency.

## Core capabilities

- **Reader fleet lifecycle** — discovery, manual registration, probe, enable/disable, activation, removal, state snapshots, and recovery after faults.
- **Capability-driven settings** — RF mode, power, sensitivity, antennas, Gen2 session/population, filters, reports, GPI triggers, GPO state, and applicable vendor settings are generated from the active reader capability snapshot.
- **Inventory** — one or many readers, long-running sessions, explicit stop reasons, bounded aggregation, EPC/TID/RSSI/antenna/channel/time fields, optional raw JSONL logging, and final inventory snapshots.
- **Tag Access** — platform-level read and write workflows for EPC, TID, User, and Reserved banks, gated by the reader's reported capability.
- **GPIO** — GPI status and events, GPI-triggered inventory stop, and GPO control when the device exposes the required ports.
- **Local persistence** — EF Core SQLite stores reader profiles, settings presets, tag lists, inventory runs, and application settings.
- **Vendor modules** — Impinj is the maintained extension path; Zebra is integrated as an experimental module pending broader physical calibration.
- **Diagnostics** — layered application/service/SDK logging, stable platform error codes, inventory run history, and an auxiliary virtual-device packet inspector.

## Architecture

![LlrpReaderPlatform architecture](docs/assets/architecture.svg)

The core is UI-independent:

~~~text
Application composition roots
  -> Contracts
  -> Services -> Contracts + LlrpSdk
  -> Infrastructure -> Services + Contracts
  -> Extensions.* -> Services + Contracts + vendor SDK packages
~~~

- **Contracts** contains immutable DTOs, capability and settings semantics, stable error codes, persistence contracts, and public service interfaces. It has no WPF, SDK, or vendor dependency.
- **Services** owns reader lifecycle, session leases, settings compilation, inventory, Tag Access, GPIO, extension resolution, and projection from SDK objects to platform contracts.
- **Infrastructure** implements SQLite persistence, Zeroconf discovery, logging, snapshots, and tag logs.
- **Extensions.Impinj** and **Extensions.Zebra** contribute vendor matching, features, settings, and report fields without adding vendor types to Contracts.
- UI projects are composition roots and consumers. ViewModels and Razor components do not create SDK readers or own TCP sessions.

Architecture tests enforce the dependency direction and prevent SDK or UI types from leaking into public Contracts.

### Reader ownership and concurrency

Each registered reader has one **ReaderHandle**, one per-reader operation gate, and at most one active TCP session.

- **Probe** uses a temporary session and can hand a successful standard session to activation.
- **Settings, Tag Access, and GPIO** use short leases: connect, execute, normalize the result, and disconnect.
- **Inventory** uses one long lease from Start until Stop or fault; all reports come from that same InventorySession.
- A conflicting short operation returns the stable **ReaderBusy** result. The platform does not silently stop or restart inventory.
- Different readers keep independent gates and can operate concurrently.
- Faulted or stale sessions are disposed before a later operation performs a fresh probe and extension match.

This lifecycle is implemented once in Services and shared by WPF and Blazor consumers.

## Device and protocol boundary

Compatibility is claimed by layer and by physical evidence:

| Layer | Meaning |
|---|---|
| **L1** | TCP, LLRP handshake, protocol version, identity, and standard capability query |
| **L2** | Standard inventory and tag observations |
| **L3** | Standard settings, Gen2 filters, Tag Access, and GPI/GPO |
| **L4** | Vendor extensions such as Impinj Search Mode, FastID, phase, and low-duty-cycle controls |

Current baseline:

| Target | State |
|---|---|
| **Impinj R420 / LLRP 1.0.1** | Main physical baseline. Connection, standard and Impinj settings, inventory, FastID/phase reports, tag-memory reads, User-bank write/restore, GPI status, and GPO control have recorded evidence. Some physical GPI-trigger, multi-reader inventory, and fault-recovery scenarios remain explicit acceptance items. |
| **Standard LLRP 1.0.1 reader** | Probe, activation, capability/settings query, and settings write-back have physical evidence on the maintained standard-reader baseline. Inventory and Tag Access remain device/antenna dependent. |
| **LLRP 1.1 and 2.0** | Connection policies and standard capability-gating infrastructure are present. They remain PendingHardware in this platform until suitable physical readers are accepted. |
| **Zebra FX9600** | Standard connection and identity have physical evidence. The platform module is experimental and does not claim L4 support yet. |

The [device compatibility matrix](docs/compatibility/device-matrix.md) is authoritative. Automated tests, virtual readers, SDK mappings, or a vendor name do not raise a device's support level.

## Run the primary WPF client

Requirements:

- Windows with the .NET 10 SDK for source builds;
- network access to an LLRP reader, normally on TCP port 5084.

The repository uses published LLRPCSharp NuGet packages by default:

~~~powershell
dotnet restore src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj -p:UseLocalLlrpSdk=false
dotnet run --project src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj -p:UseLocalLlrpSdk=false
~~~

On first run, the application creates its SQLite database, logs, and inventory data under:

~~~text
%LocalAppData%\LlrpReaderPlatform\
~~~

Typical workflow:

1. Discover or add a reader under **Data Sources**.
2. Select the protocol policy, probe the endpoint, and enable the reader.
3. Open **Reader Settings**, load current values or SDK defaults, edit only capability-supported fields, and apply.
4. Start one or more readers under **Inventory** and observe live tags and aggregate statistics.
5. Use **Tag Memory**, **Tag Lists**, **Inventory Runs**, and **Diagnostics** as needed.
6. Stop inventory explicitly before settings, Tag Access, or GPIO operations on the same reader.

See the [WPF user and troubleshooting guide](docs/development/wpf-user-and-troubleshooting.md) for operational details.

## Other clients and virtual readers

### MAUI Blazor Hybrid

The MAUI application targets Windows and Android from the main project and Mac Catalyst on macOS. It reuses Contracts, Services, Infrastructure, and vendor modules; responsive pages change presentation, not reader lifecycle.

~~~powershell
dotnet build src/LlrpReaderManager/LlrpReaderManager.csproj -f net10.0-windows10.0.19041.0
dotnet run --project src/LlrpReaderManager/LlrpReaderManager.csproj -f net10.0-windows10.0.19041.0
~~~

Android and Mac Catalyst require their corresponding MAUI workloads and platform toolchains. The Linux head additionally requires GTK4, WebKitGTK, a compatible .NET runtime, and the preview MAUI Linux backend. See [ReaderManager development mode](docs/development/reader-manager.md).

### Virtual Reader development paths

The repository contains two deliberately different virtual-reader mechanisms:

- **LlrpReaderPlatform.VirtualReader** is an in-process IReaderSession implementation for deterministic Services/UI development. It does not listen on TCP and is not an external LLRP endpoint.
- **LlrpVirtualDevice.App.Wpf** manages real TCP/LLRP virtual endpoints from the LLRPCSharp Virtual Device Hosting package. The primary client connects to those endpoints exactly as it connects to hardware.

Run the protocol-level virtual-device manager on Windows:

~~~powershell
dotnet run --project src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj -p:UseLocalLlrpSdk=false
~~~

See [Virtual Reader development mode](docs/development/virtual-reader.md) for scenarios, persistence, and packaging.

## Build and test

The full solution includes WPF, MAUI, Linux GTK4, shared libraries, and tests. Install the workloads required by the projects you intend to build.

~~~powershell
dotnet restore LlrpReaderPlatform.slnx -p:UseLocalLlrpSdk=false
dotnet build LlrpReaderPlatform.slnx --no-restore -p:UseLocalLlrpSdk=false
dotnet test LlrpReaderPlatform.slnx --no-build -p:UseLocalLlrpSdk=false
~~~

Warnings are treated as errors. Automated coverage includes Contracts, Services, Infrastructure, WPF ViewModels, architecture boundaries, vendor modules, and the in-process Virtual Reader. Physical-reader acceptance is a separate workflow described in the [hardware validation runbook](docs/development/hardware-validation-runbook.md).

### Develop against a local LLRPCSharp checkout

For cross-repository debugging, either pass properties explicitly:

~~~powershell
dotnet build LlrpReaderPlatform.slnx -p:UseLocalLlrpSdk=true -p:LlrpSdkSourceRoot=..\LLRPCSharp
~~~

or copy **Directory.Build.local.props.example** to the ignored **Directory.Build.local.props** and edit the source path. CI and release builds always use **UseLocalLlrpSdk=false** so release artifacts come from published SDK packages.

## Repository map

~~~text
src/
  LlrpReaderPlatform.Contracts/          public domain contracts
  LlrpReaderPlatform.Services/           reader and operation orchestration
  LlrpReaderPlatform.Infrastructure/     SQLite, discovery, logging, snapshots
  LlrpReaderPlatform.Extensions.Impinj/  Impinj platform module
  LlrpReaderPlatform.Extensions.Zebra/   experimental Zebra module
  LlrpReaderPlatform.VirtualReader/      in-process development reader

  LlrpReaderPlatform.App.Wpf/            primary Windows client
  LlrpReaderManager/                     MAUI Blazor Hybrid client
  LlrpReaderManager.Linux/               experimental Linux GTK4 head
  LlrpVirtualDevice.App.Wpf/             TCP virtual-device manager

tests/                                   contract, service, UI, architecture, extension,
                                         virtual-reader, and hardware validation projects
docs/                                    architecture, ADRs, development, compatibility,
                                         release, and migration documentation
~~~

## Releases

This repository publishes applications rather than platform NuGet packages:

- self-contained Windows x64 WPF client and virtual-device manager;
- MAUI Blazor Windows package, Android APK, and Mac Catalyst application archives;
- framework-dependent Linux x64 Debian package;
- checksums and release notes.

Mac Catalyst artifacts are currently unsigned and unnotarized. The Linux package depends on compatible .NET, GTK4, and WebKitGTK runtimes. See the [release specification](docs/development/release.md) for exact artifact and platform requirements.

## Documentation

- [Documentation index](docs/README.md)
- [Project vision](docs/llrp-framework-vision.md)
- [Architecture overview](docs/architecture/overview.md)
- [Reader lifecycle and ownership](docs/architecture/reader-runtime.md)
- [Extensions and settings model](docs/architecture/extensions-and-settings.md)
- [Testing strategy](docs/development/testing-strategy.md)
- [Device compatibility matrix](docs/compatibility/device-matrix.md)
- [Hardware validation runbook](docs/development/hardware-validation-runbook.md)
- [Release specification](docs/development/release.md)
- [UI overview and release assets](docs/ui-overview.md)
- [ADR index](docs/decisions/README.md)
