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

## Phase 1, slice 2

The slice 2 tools are implemented with the same version 1 response envelope,
deterministic ordering, and structured error objects:

- `decompile_script` accepts a script start address and returns the
  decompiled text. It verifies the address is an `XSERun` script start;
  invalid addresses return `invalid_argument` rather than throwing.
- `list_constants` supports `items`, `species`, `sprites`/
  `object_graphics`, `movement_types`, `flags`, and `vars`/
  `variables`. Each value contains a numeric value, optional name, and
  `NameSource`. HMA combines ROM metadata and reference lists while building
  these options, so the per-entry source label is an implementation-level
  provenance classification, not a byte-level proof.
- `search_script_commands` searches parser commands case-insensitively and
  also reads documented multicommands from `resources\scriptReference.txt`.
  This fallback is needed for commands such as `giveitem`, which are
  documented there but are not active parser command lines.

The CLI form for every tool is:

```powershell
dotnet artifacts\HexManiac.Agent\bin\Debug\net6.0\HexManiac.Agent.dll call <tool> <rom> '<json args>'
```

Verified real outputs included:

- FireRed `decompile_script` address `1464156` returned a multi-section script
  beginning `section0: # 16575C`, with `msgbox.default`, movement data,
  comparisons, and `setflag 0x0002`.
- Emerald `decompile_script` address `1950717` returned the Petalburg object
  script beginning `section0: # 1DC3FD`, including the WALLY text and `end`.
- `search_script_commands` for `applymovement` returned `applymovement` with
  `npc: Short, data: Pointer` and `applymovement2` with the additional
  `bank: Byte, map: Byte` arguments.
- `search_script_commands` for `giveitem` returned `giveitem`, `giveitem2`,
  and `giveitem3` from the reference-file multicommand definitions.

Free-value allocation uses HMA's `Flags.NextFreeFlag` and
`Flags.NextFreeVariable` procedures over explicit ranges. The default flag
range is `persistent-story` (`0x21`-`0x4FF`, decimal 33-1279); temporary
flags (`0x00`-`0x20`), trainer flags (`0x500`-`0x7FF`), system flags
(`0x800`-`0xFFFF`), and `all` are selectable with the optional `range`
argument. The default variable range is `persistent`
(`0x4034`-`0x40FF`, decimal 16436-16639); `all` selects
`0x4034`-`0x7FFF`.

On the FireRed fixture, the historical un-ranged slice-1 call returned flag
33 (`0x21`) and variable 16462 (`0x404E`). Those values came from
`Flags.GetScriptUsage` followed by `Flags.NextFreeFlag`/
`Flags.NextFreeVariable`; the flag was the first candidate in the classic
non-temporary range, while the preceding variables were occupied by script,
event, thumb-variable, or named-constant usage. The hardened range-aware
implementation still reports flag 33 in `persistent-story`; its current
runtime variable result is the first free persistent candidate, 16436
(`0x4034`), because the explicit range result is now reported directly.

The free-flag test scans parser-recognized script flag usage, hidden-item
flags, trainer flags, reserved named flags, and every map object-event flag.
It cannot detect arbitrary flag use in compiled game code outside the script
and event metadata understood by HMA. The variable test covers HMA's script,
event, thumb-variable, and reserved-variable usage.

The final validation commands were:

```powershell
dotnet build HexManiacAdvance.sln -c Debug
dotnet test HexManiacAdvance.sln -c Debug --no-build `
  --filter FullyQualifiedName~HexManiac.Agent.Tests `
  --logger "console;verbosity=minimal"
```

The solution build passed with nullable-analysis warnings and zero errors.
Agent tests passed: 17 passed, 0 skipped, 0 failed. CUBE runtime comparison
remains **NOT RUN** because no CUBE ROM was supplied. The adjacent-ROM TOML
positive path is implemented but remains **NOT RUN** because neither supplied
vanilla ROM has an adjacent `.toml`.

## Phase 2, slice 1: compile dry run

`compile_script` accepts `text` and an optional `address` and compiles against
a new model loaded from a copy of the ROM bytes. It never passes the real ROM
model to `ScriptParser.Compile`. Errors contain a line number and message,
while warnings are returned separately. The real ROM bytes and
`ViewPort.ChangeHistory` remain unchanged after every call.

Verified dry-run results:

- `end` returned `Ok: true`, `ByteSize: 1`, and empty errors/warnings.
- `not_a_real_command` returned `Ok: false`, line 1, with
  `not_a_real_command is not a valid command`.
- `end 1` returned `Ok: false`, line 1, with an argument-count error.
- `setflag DOES_NOT_EXIST` returned `Ok: false`, line 1, with a structured
  number/constant parse error.

The round-trip test decompiled and recompiled the first 200 reachable scripts
for each supplied ROM. FireRed matched 200/200 exactly. Emerald matched
197/200 exactly; the three mismatches were addresses 1954296 (347 original
bytes and 347 compiled bytes), 1955292 (103 and 103), and 1988837 (25
original bytes and 24 compiled bytes). These are byte-layout/decompilation
normalization differences rather than writes to the ROM.

The free-variable proof now runs on both ROMs. It checks
`Flags.GetScriptUsage`, which covers parser-recognized variable commands,
reserved variables, thumb variables, and script-event trigger variables, and
also calls `Flags.FindVarUsages`. The returned FireRed value is 16436
(`0x4034`); Emerald returns 16462 (`0x404E`). It cannot detect arbitrary
variable use in compiled game code or fields that HMA does not expose as
script/event metadata.

The earlier FireRed value 16462 came from the previous allocation path and
was not caused by a ROM write. The current range-aware implementation returns
the first free value in the persistent range after HMA's usage scan; the
proof test verifies that 16436 is not referenced. Thus 16436 is the correct
current result for FireRed's deterministic lowest-free search; 16462 remains
inside the range but is not the first candidate.
