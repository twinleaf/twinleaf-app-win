# What belongs in `twinleaf-rust`

Notes from porting the core to Windows, on which parts of `rust/tio-bridge`
are app code and which are library code sitting in the wrong crate.

Everything here applies to the macOS app equally — both apps build the same
`tio-bridge` source, so any of these changes lands once and benefits both (and
the GTK build, once it catches up to `twinleaf` 2.0).

Ordered by value.

---

## 1. Move `spectral` out of `twinleaf-tools` into the `twinleaf` library

**The strongest case, and the cheapest to act on.**

The app uses exactly two things from `twinleaf-tools`:

```rust
use twinleaf_tools::tui::spectral::WelchOp;                     // main.rs:32
twinleaf_tools::tui::spectral::estimate_white_noise_floor(&pairs) // main.rs:7024
```

`twinleaf-tools` is the CLI/TUI crate. Depending on it for two functions drags
its whole terminal stack into a shipped GUI application. Measured on this repo
with `cargo tree`:

| | crates |
| --- | --- |
| Whole app dependency graph | 190 |
| Reachable via `twinleaf` (the library) | 81 |
| **Reachable *only* via `twinleaf-tools`** | **108** |

Those 108 include `ratatui`, `ratatui-crossterm`, `crossterm`, `tui-prompts`,
`clap`, `clap_complete`, `dialoguer`, `indicatif`, `console`, `color-eyre`,
`tracing-subscriber`, `env_logger`, `nucleo-matcher`, `termtree` — a terminal UI
toolkit, an argument parser and a tracing stack, none of which the app can
reach. `tio-bridge/Cargo.toml` already admits it:

> Lib target only, for `tui::spectral` … Pulls the tools' CLI/TUI dependency
> stack along; **none of it is invoked**.

Roughly six of the 108 are genuine transitive needs of the spectral code itself
(`rustfft`, `num-complex`, `transpose`, `strength_reduce`, `primal-check`,
`num-integer`) and would move with it. The other ~100 are pure carry.

**Proposal.** Move `spectral` to `twinleaf::data::spectral` and have
`twinleaf-tools` re-export it. It is spectral math, not UI — the fact that it
lives under a module named `tui` is the tell. It also belongs next to
`data::pipeline`, since `WelchOp` is a `ColumnOp` implementation and `ColumnOp`
is already defined there.

**Effect.** The app depends only on `twinleaf`; the dependency graph roughly
halves. Smaller binary, faster cold builds, and a much smaller surface to audit
or license-review for a shipped instrument app.

**Risk.** Low, and mechanical — a module move plus a re-export. Nothing in the
tools crate changes behaviourally.

---

## 2. Promote the stream-health math

`tio health` and the app compute the same diagnostics, and the code is
*byte-for-byte identical* in places. `OnlineSlope` — the incremental
least-squares slope used for clock drift — appears twice:

- `twinleaf-tools/src/tools/health.rs:157` — private, inside the TUI tool
- `rust/tio-bridge/src/main.rs:3241` — the app's copy

The two `push`/`slope`/`reset` bodies are the same code. The constants are
duplicated too: upstream's `MIN_DRIFT_SAMPLES = 50` is the app's
`HEALTH_MIN_DRIFT_SAMPLES = 50`. The same story holds for the jitter window
(`TimeWindow` upstream, `JitterWindow` in the app) and the per-stream
rate/jitter/drift/gap accounting (`StreamStats` upstream, `StreamHealth` in the
app).

The app did not reimplement these out of preference — upstream's versions are
private items inside a binary crate, so there was nothing to call.

**Proposal.** Extract the measurement math — slope, jitter window, per-stream
rate/drift/dropped accounting — into a public `twinleaf::data::health`, keeping
the *presentation* (colours, TUI rows, boundary reasons) in `twinleaf-tools`.
Then `tio health` and all three apps report identical numbers by construction
rather than by two implementations happening to agree.

**Risk.** Low. Extract-and-reuse with the TUI left alone; upstream's own tests
pin the behaviour.

---

## 3. Consider promoting the multi-sensor mount

`main.rs` can serve several sensors as one device tree on a loopback TCP port
(sensor *k* mounted at route `/k`), so a set of independent sensors behaves
exactly like a hub. That is protocol plumbing, not UI, and it sits directly on
`tio::proxy`.

It is plausibly useful to `tio proxy` itself and to any script driving several
sensors at once.

**Proposal.** Worth promoting to `twinleaf::tio`, but lower priority than 1 and
2 — unlike those it is not duplicated anywhere, so the win is reuse rather than
de-duplication. Confirm a second caller wants it before moving it.

---

## What should stay in the app

- **FPCS decimation (`StreamingFpcs`).** Tempting, but `data/pipeline.rs`
  explicitly designates this as app territory: "`ColumnOp` is what app crates
  implement for stateful streaming computations (decimation, Welch, …)". The
  app already implements `ColumnOp`, so it is using the extension point as
  designed. If the three apps' copies drift, revisit — but as a *provided* op
  alongside `WelchOp`, not as a change to the contract.
- **Packet logging and playback.** Thin wrappers over `data::reader` and the
  `.tio` format; the app-specific part is the recording lifecycle (temporary
  backing file, relocate-on-save), which is document behaviour, not protocol.
- **`CoreCommand`/event orchestration.** This *is* the app's core.

## Already shared — no action

- **CSV/HDF5 export** — already `twinleaf::data::export`.
- **Firmware check and flash** — already `twinleaf::firmware`.
- **Discovery, RPC, proxy, parsing** — already `twinleaf`.

---

## Suggested order

1. Move `spectral` into `twinleaf` (biggest win, lowest risk, unblocks a much
   leaner app build).
2. Extract `twinleaf::data::health` and retire the duplicate.
3. Revisit the mount and FPCS once a second consumer actually asks.
