# Twinleaf for Windows

A native Windows app for discovering, streaming, plotting, logging and
configuring Twinleaf quantum sensors.

This is the Windows sibling of the macOS/iPadOS [`twinleaf-app`](../twinleaf-app),
built the same way: a **native platform UI** over the **same Rust core**, with a
C ABI between them.

| | macOS / iPadOS | Windows |
| --- | --- | --- |
| UI | SwiftUI | WinUI 3 (Windows App SDK), C# |
| Core | `libtwinleaf_core.dylib` / `.a` | `twinleaf_core.dll` |
| Boundary | C ABI (`twinleaf_core.h`) | the same C ABI |
| Drawing | SwiftUI `Canvas` | Win2D (Direct2D) |

Keeping the boundary identical is the point: the core is developed once, and
the two apps stay behaviourally identical because they consume the same events.

## Architecture

```
twinleaf-app-win/
  rust/tio-bridge/       # Rust core — copied from the macOS app, unmodified
    src/lib.rs           #   C ABI shim (twinleaf_runtime_*)
    src/main.rs          #   runtime + CLI harness
    include/twinleaf_core.h
  src/Twinleaf/          # WinUI 3 app
    Interop/             #   P/Invoke + binary event decoding
    Models/              #   device, stream, column, RPC model
    Core/BridgeClient.cs #   observable app model
    Controls/            #   plot canvas, channel tree, splitter
  vendor/twinleaf-rust/  # submodule, pinned to the macOS app's commit
```

### The boundary

The core owns everything hardware-facing: device I/O through `twinleaf-rust`,
FPCS decimation, Welch FFT, `.tio` packet logging, CSV/HDF5 export, playback,
firmware updates and stream health. The UI drives it by calling
`twinleaf_runtime_*` and consuming events through one callback.

Events arrive in three shapes, discriminated by the callback's `kind`:

| kind | transport | used for |
| --- | --- | --- |
| 0 | UTF-8 JSON | events with no binary encoding (firmware progress, stream health) |
| 1 | binary plot frame | the streaming hot path — already-decimated series |
| 2 | binary typed event | the sixteen common events (device list, metadata, RPC results, …) |

`Interop/EventDecoder.cs` is the mirror image of the Rust encoders in
`rust/tio-bridge/src/main.rs`. **The wire format carries no field names**, so
field order there is load-bearing — reordering silently misreads every later
field. When the core's encoders change, that file changes with them.

Threading: the core calls back from its own worker threads and the payload
pointer is valid only for that call, so events are decoded synchronously on the
Rust thread and the immutable result is marshalled to the UI thread by
`BridgeClient`.

## Build

Prerequisites:

- **Rust** (MSVC toolchain) — <https://rustup.rs>. The core links against MSVC,
  so this must be `x86_64-pc-windows-msvc`, *not* an MSYS2/MinGW Rust.
- **.NET 9 SDK** — <https://dot.net>.
- **Visual Studio Build Tools 2022** with the MSVC toolset and a Windows 10/11
  SDK, which is what Rust's MSVC target links through.
- The vendored core: `git submodule update --init --recursive`.

```sh
dotnet build src/Twinleaf/Twinleaf.csproj -r win-x64
```

The build compiles the Rust core first and stages `twinleaf_core.dll` beside the
app, the same way the macOS app's Xcode run-script phase embeds the dylib. Pass
`-p:TwinleafSkipRustBuild=true` to reuse the last Rust build.

To produce a runnable, self-contained app folder:

```sh
dotnet publish src/Twinleaf/Twinleaf.csproj -c Release -r win-x64
```

That output is unpackaged and self-contained — no MSIX install, no separately
installed Windows App SDK or .NET runtime — so it can be zipped and run
anywhere.

### Testing without hardware

`twinleaf-rust` ships a simulator. Run it, then connect the app to
`udp://127.0.0.1:7855` (use `127.0.0.1`, not `localhost` — the simulator binds
IPv4):

```sh
cargo run --manifest-path vendor/twinleaf-rust/Cargo.toml --bin tio -- simulate
```

The Rust core also builds a CLI harness (`tio-bridge`) that speaks the same
events over stdout, which is useful for isolating core behaviour from the UI.

## Status

Working end to end:

- Rust core builds unmodified on Windows as `twinleaf_core.dll`, exporting all
  sixteen `twinleaf_runtime_*` entry points.
- Full P/Invoke surface, binary typed-event decoder, and binary plot-frame
  decoder (frame version 5, with the older version gates the Swift app carries).
- Live device discovery, connect/disconnect, stream + column metadata, channel
  sidebar with live value readout, and a Win2D plot with linear/log axes, nice
  ticks, legend and crosshair readout.

Not yet ported from the macOS app: the RPC sidebar, `.tio` document
open/playback scrubber, CSV/HDF5 export UI, firmware-update popover, multi-pane
layout with drag-and-drop, and preferences.

## License

Apache-2.0. Vendored dependencies retain their own licenses; `vendor/twinleaf-rust`
is MIT/Apache-2.0.
