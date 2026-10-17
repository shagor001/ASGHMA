# AI assistant integration

## Phase 0: headless map inspection and write spike

The spike is implemented in `src/HexManiac.Agent` and
`src/HexManiac.Agent.Tests`. Both target `net6.0`, reference `HexManiac.Core`
directly, and do not reference the WPF project.

Commands:

```powershell
dotnet build src\HexManiac.Integration\HexManiac.Integration.csproj
dotnet build src\HexManiac.Agent\HexManiac.Agent.csproj
dotnet run --project src\HexManiac.Agent -- inspect "C:\Users\iamsh\ROMs\Pokemon Fire Red (U).gba" "maps.bank3.Pallet Town.3-0"
dotnet run --project src\HexManiac.Agent -- list-maps "C:\Users\iamsh\ROMs\Pokemon Emerald Version (U).gba"
dotnet run --project src\HexManiac.Agent -- inspect "C:\Users\iamsh\ROMs\Pokemon Emerald Version (U).gba" "maps.bank0.PETALBURG CITY.0-0"
dotnet test src\HexManiac.Agent.Tests\HexManiac.Agent.Tests.csproj --no-build --logger "console;verbosity=normal"
dotnet test src\HexManiac.Integration\HexManiac.Integration.csproj --logger "console;verbosity=minimal"
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
- The standalone Agent and Agent.Tests projects both built successfully.
- After correcting the fixture path, the headless write test passed with
  `Passed: 1, Skipped: 0, Failed: 0`.
- The Integration project build/test did not reach test execution because the
  existing `HexManiac.Tests` project fails compilation. The observed failure
  was 1,816 compile errors, primarily missing `HavenSoft.HexManiac.Core`
  namespace/type references, plus two `CS0535` comparer errors. Therefore the
  Integration counts are Passed: 0, Skipped: 0, Failed: 0 executed tests;
  the run is **blocked at compilation**, not a test failure. No existing HMA
  source files were changed to work around it.

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

## Follow-up status

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

The exact required solution command was run:

```powershell
dotnet build HexManiacAdvance.sln -c Debug
```

It remains **FAILED** on this branch. `master` was built with the same command
in a clean detached worktree and **PASSED**. The new Agent projects now build
without copying Core `.cs` sources, but the solution-level build reaches the
existing `HexManiac.Integration`/`HexManiac.Tests` reference and fails with
missing `HavenSoft.HexManiac.Tests` or Core test symbols depending on solution
build ordering. This is a build-system/project-reference issue, not a ROM
runtime failure. Existing HMA projects were not edited.

The required Integration command:

```powershell
dotnet test src\HexManiac.Integration\HexManiac.Integration.csproj
```

is **FAILED before test discovery** because `HexManiac.Tests` does not compile.
Counts are Passed: 0, Skipped: 0, Failed: 0 executed tests; compilation
reported hundreds of errors, so no test cases ran.

The updated FireRed inspection command was run successfully and verified the
new output:

```powershell
dotnet run --project src\HexManiac.Agent -- inspect `
  "C:\Users\iamsh\ROMs\Pokemon Fire Red (U).gba" `
  "maps.bank3.Pallet Town.3-0"
```

The output contained three objects, three warps, three scripts, and five
signposts. The object with no script emitted `"scriptAddress": null`, and all
signposts emitted typed fields.

Phase 1 read-only JSON methods (`get_map`, `list_events`,
`decompile_script`, `list_constants`, `search_script_commands`,
`find_free_flag`, and `find_free_var`) are **NOT RUN** and were not added.
