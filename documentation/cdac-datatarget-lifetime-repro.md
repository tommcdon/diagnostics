# cDAC SOS DataTarget lifetime repro

## Summary

The cDAC SOS/`IXCLRDataProcess` path does not deterministically release the
debugger host's `ICLRDataTarget` when the final external COM reference reaches
zero. The generated managed COM wrapper stops rooting the managed cDAC object,
but the imported native COM wrappers are released later by GC finalization.

The legacy DAC releases the same host interface synchronously.

## User-visible symptom

Concord detects its native data target still alive at debugger-session
shutdown. The observed leak count is two in the legacy-routed cDAC path:

1. `ClrDataAccess::m_pTarget`
2. `CDAC::m_target`

The same underlying issue is present in the SOS native host. Existing SOS tests
pass because they validate command behavior, not native COM ownership after
the final `IXCLRDataProcess::Release`.

## Debug CRT evidence

This branch instruments the Windows Debug SOS host so `DataTarget` is allocated
through the Debug CRT and reports its state immediately after SOS releases its
cached cDAC `IXCLRDataProcess`.

With the pre-fix cDAC:

```text
SOS_CRT_AFTER_CDAC_RELEASE_REFS=0 LIVE=1 CRT_BLOCK=1 REQUEST=97
Detected memory leaks!
... datatarget.cpp ... {97} normal block ... 72 bytes long.
SOS_CRT_DATATARGET_LIVE=1
```

The external cDAC COM reference count has reached zero, but the host-owned
`DataTarget` remains allocated.

With the runtime prototype:

```text
SOS_CRT_AFTER_CDAC_RELEASE_REFS=0 LIVE=0 CRT_BLOCK=0 REQUEST=0
SOS_CRT_DATATARGET_LIVE=0
```

No forced GC or runtime shutdown is required.

## Why the fix belongs in cDAC

SOS follows the normal COM ownership contract:

1. It creates and owns one data-target reference.
2. It passes the target to cDAC.
3. It releases its own target reference.
4. It releases every queried DAC interface.

cDAC acquires additional references while importing the target into managed
COM. Those references are cDAC-owned and must be released by cDAC.

Adding compensating `Release` calls in SOS or Concord would be unsafe. Managed
finalization could later release the same references, causing over-release or
use-after-free.

## Runtime prototype

The prototype is in `tommcdon/runtime`:

- Branch: `dev/tommcdon/cdac-sos-deterministic-release`
- Commit: `d4972cb2f894176ec41170cedbfaf3d8f26ed693`
- URL:
  <https://github.com/tommcdon/runtime/tree/dev/tommcdon/cdac-sos-deterministic-release>

It:

1. Imports caller-owned native interfaces with
   `UniqueComInterfaceMarshaller`.
2. Stores those wrappers in an idempotent `NativeInterfaceOwner`.
3. Exposes `SOSDacImpl` with a cDAC-specific `ComWrappers` implementation.
4. Hooks the generated `IUnknown::Release`.
5. Calls `FinalRelease` on the owned native wrappers when the final external
   COM reference reaches zero.
6. Leaves the managed cDAC object graph and memory to normal GC.

The legacy-routed path releases the unique legacy DAC wrapper first. Destroying
the legacy `ClrDataAccess` then releases its target references. The direct cDAC
path releases the unique data-target wrapper directly.

DBI remains unchanged: it continues using its explicit `Destroy` operation.
The cleanup mechanism is similar, but the trigger differs because
`IXCLRDataProcess` has no corresponding public `Destroy` API.

## Diagnostics repro branch

The Debug-CRT instrumentation, repeatable EngineHost script, and this write-up
are in `tommcdon/diagnostics`:

- Branch: `dev/tommcdon/cdac-datatarget-crt-leak-repro`
- URL:
  <https://github.com/tommcdon/diagnostics/tree/dev/tommcdon/cdac-datatarget-crt-leak-repro>

## Validation

The runtime prototype passed:

- Same-commit NativeAOT Debug cDAC build.
- 3,408 cDAC managed unit tests.
- Native direct and legacy-routed cDAC reference-count probes.
- The SOS Debug-CRT repro on this branch.
- Four existing SOS RuntimeInfo cDAC tests.

## Running the SOS repro

Build the Windows x64 Debug native diagnostics product:

```powershell
.\eng\Build-Native.cmd -architecture x64 -configuration Debug
```

Ensure the SOS harness has built its EngineHost and a net11 scenario dump, then
run:

```powershell
.\eng\repro-cdac-datatarget-leak.ps1 `
  -CDacDirectory D:\path\to\candidate-cdac-pair
```

`CDacDirectory` must contain the matched pair:

- `mscordaccore_universal.dll`
- `mscordbi_universal.dll`

The pre-fix result has `LiveAfterRelease=1` and `CrtLeakReported=True`. The
prototype result has `LiveAfterRelease=0` and `CrtLeakReported=False`.
