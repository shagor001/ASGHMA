# AI assistant integration

## Phase 0: headless map inspection and write spike

The spike is implemented in `src/HexManiac.Agent` and
`src/HexManiac.Agent.Tests`. Both target `net6.0`, reference `HexManiac.Core`
directly, and do not reference the WPF project.

The required validation command is:

```powershell
dotnet build HexManiacAdvance.sln -c Debug
```

`inspect` loads the ROM with `HardcodeTablesModel`, creates a `ViewPort`
using `InstantDispatch`, `MapTutorialsViewModel`, and `StubFileSystem`, waits
for `InitializationWorkload`, navigates with `Goto`, and serializes the
primary map's object, warp, script, and signpost events as JSON. The command
sets the working directory to the agent output directory so Core's existing
`resources` files are found. It does not require a display or the WPF
assembly.

`list-maps` prints every map as `maps.bank<bank>.<name>.<bank>-<map>`, which
is the exact format accepted by `inspect`.

### Verified by running

- The FireRed `inspect` command succeeded for
  `maps.bank3.Pallet Town.3-0` and emitted JSON containing 3 objects, 3
  warps, 3 scripts, and 5 signposts.
- The Emerald `list-maps` command succeeded. Its first entries included
  `maps.bank0.PETALBURG CITY.0-0` through
  `maps.bank0.ROUTE 104.0-19`.
- Emerald `inspect` succeeded for
  `maps.bank0.PETALBURG CITY.0-0` and emitted JSON containing 9 objects, 6
  warps, 8 scripts, and 8 signposts.
- `HeadlessWriteTests.CreateNpc_Undo_RestoresOriginalRomBytes` passed. It
  loaded FireRed, navigated to Pallet Town, created an object event, applied
  `EventTemplate.CreateNPC`, observed the object count increase by one, then
  undid the change and verified every ROM byte matched the original.
- `dotnet build HexManiacAdvance.sln -c Debug` passed after the Phase 1 slice
  fixes. It produced two nullable-analysis warnings in the test project and
  zero errors.
- The solution-level Agent test command passed:

  ```powershell
  dotnet test HexManiacAdvance.sln -c Debug --no-build --filter FullyQualifiedName~HexManiac.Agent.Tests --logger "console;verbosity=minimal"
  ```

  Result: Passed 5, Skipped 0, Failed 0. The other solution test assemblies
  were selected by the solution runner but had no matching tests.

### Phase 0 findings

- `CreateObjectEvent` and `EventTemplate.CreateNPC` are **verified
  headless-compatible** by the passing write/undo test. They use the model,
  `ModelDelta` history token, and view-model callbacks; no WPF-only type was
  needed.
- Flags and variables are named through the model lists
  `scriptflagaliases` and `scriptvariablealiases`. This was verified by
  reading `Flags.cs` and the CUBE metadata definitions; the runtime commands
  above did not allocate a new flag or variable.
- To find an unused flag, start with `Flags.GetScriptUsage(model, parser)` so
  script, trainer, and variable usage is collected; include event flags and
  reserved named constants through `Flags.GetReservedFlags(model)`; then call
  `Flags.NextFreeFlag(model, used)`. It prefers a CUBE custom pool when the
  metadata contains a name such as `_CUSTOM_`, otherwise it starts at the
  classic flag range and only falls back to any unused value if the
  free-named range is full. `Flags.NextFreeVariable` follows the analogous
  variable path.
- CUBE differs from vanilla Emerald in metadata-driven table layouts,
  constants, script aliases, command definitions, and custom flag/variable
  pools. This was verified by reading `cube_emerald.toml`, the default
  Emerald metadata, `Flags.cs`, and the script reference files. **A runtime
  comparison against a CUBE ROM remains unverified** because no CUBE ROM was
  supplied.

## Phase 1, slice 1

The supplied files are vanilla ROMs:

- `C:\Users\iamsh\ROMs\Pokemon Fire Red (U).gba`
- `C:\Users\iamsh\ROMs\Pokemon Emerald Version (U).gba`

No CUBE ROM is being used, so CUBE runtime comparison is **NOT RUN**.

The inspect schema now emits JSON `null` for `Pointer.NULL` script addresses.
Signposts are decoded by kind: kinds 0-4 expose `scriptAddress`, kinds 5-7
expose `itemId`, `hiddenItemId`, `quantity`, and `isUnderFoot`, and kind 8
exposes `secretBaseId`. Warp entries always contain an `id` key; a non-positive
underlying warp ID is represented as JSON `null` because the source format uses
it as a sentinel.

ROM-adjacent metadata loading is implemented: when `<rom>.toml` exists, its
lines are passed to `StoredMetadata`; otherwise an empty metadata set is used.
No adjacent `.toml` was present for the supplied vanilla ROMs, so this
positive metadata-file path is **not runtime-verified**.

The new read-only methods are `get_map`, `list_events`, `find_free_flag`, and
`find_free_var`. They return schema-versioned response objects:
`SchemaVersion`, `Ok`, `Result`, and structured `Error`; tool failures are
returned as JSON rather than thrown to callers. `list_events` accepts
`all`, `object`, `warp`, `script`, or `signpost` and reuses the inspect event
decoding.

The built executable is invoked without `dotnet run`:

```powershell
dotnet artifacts\HexManiac.Agent\bin\Debug\net6.0\HexManiac.Agent.dll call get_map `
  "...\Pokemon FireRed.gba" `
  '{"map":"maps.bank3.Pallet Town.3-0"}'
dotnet artifacts\HexManiac.Agent\bin\Debug\net6.0\HexManiac.Agent.dll call list_events `
  "...\Pokemon FireRed.gba" `
  '{"map":"maps.bank3.Pallet Town.3-0","type":"all"}'
dotnet artifacts\HexManiac.Agent\bin\Debug\net6.0\HexManiac.Agent.dll call find_free_flag `
  "...\Pokemon FireRed.gba" '{}'
dotnet artifacts\HexManiac.Agent\bin\Debug\net6.0\HexManiac.Agent.dll call find_free_var `
  "...\Pokemon Emerald.gba" '{}'
```

These commands ran successfully. `get_map` returned map id 3000, width 24,
height 20, layout id 78, and event counts 3/3/3/5. `list_events` returned 14
typed events. `find_free_flag` returned 33 and `find_free_var` returned 16462.
The output schema was deterministic and version 1.

The tests verify FireRed and Emerald map counts, typed FireRed events, and that
the selected free flag is absent from script usages and every map's object
events. ROM-dependent tests skip when the fixture is absent. The existing
headless write/undo test remains included and passed.

`decompile_script`, `list_constants`, and `search_script_commands` are not
implemented in this phase. CUBE runtime comparison remains not run because no
CUBE ROM was supplied. Adjacent-ROM TOML loading is implemented, but the
positive path remains unverified because the supplied vanilla ROMs have no
adjacent TOML file.
