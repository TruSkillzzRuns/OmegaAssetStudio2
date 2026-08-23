# Omega Asset Studio 2

A desktop toolkit for viewing and editing the cooked asset packages of a UE3-fork
game. Version 2 is a fresh application: the package reader, the write path, and
every tool are original to this project.

Requires a separately-installed, licensed copy of the supported game. No game
content is bundled.

## Download

Grab the latest zip from [Releases](../../releases), unzip it anywhere, and run
`OmegaAssetStudio2.exe`. Nothing else to install — the .NET runtime and the
Windows App SDK travel with it. Windows 10 1809 or later, 64-bit.

On first run, add your game install on the Home page. Several installs can be set
up side by side, and every tool has a picker choosing which one it acts on.

## Tools

| Tool | What it does |
|---|---|
| Icon Editor | Browse, preview, and replace icons, including those stored in the shared texture cache |
| Skill Recolor | Pick a character, then one of their skills, to see every colour it uses; edit and save. A file-pattern search is there for anything outside the roster |
| Voice Swapper | Browse sounds, listen, export, and swap |
| Mesh | Pick a character from the roster and see the model in 3D with its own textures, or search every package for one. Exports a model, with its skeleton and skin weights, to .fbx, .dae or .obj for editing elsewhere |
| Retarget | Bring an edited model back onto a character's skeleton and write it into the game. Size, facing, winding, unweighted vertices, over-long bone runs and the displacements powers use to reshape a model are all detected and corrected, and reported before anything is kept |
| Character Swap | Bring a costume from a newer install onto a chassis an older one has |
| Omega Backup and Restore | List every file changed, restore it, or forget the backup |

Playing back audio needs **vgmstream**, which is not bundled — see
`THIRD_PARTY_NOTICES.txt` for why. The Voice Swapper points you at the download
and finds it once it is on your machine.

## Backups

A backup sits **next to the file it protects**, named `<file>.bak`, in the same
folder — so for a game package, alongside the package inside your game install.

One copy per file, taken before that file's first change and **never
overwritten**. The tenth edit to a package still restores to how it shipped, not
to how it looked after the ninth. Omega Backup and Restore lists every one,
groups them by what they hold, says whether the live file still differs from its
backup, and puts any of them back.

## Build and run

Requires the .NET 8 SDK, pinned to 8.0.419 by `global.json`.

```powershell
.\build\build.ps1              # Release x64
.\build\build.ps1 -Test        # and run the tests
.\build\run.ps1                # build and launch
```

`-p:Platform=x64` is required if invoking `dotnet` directly. The executable lands
in `src/OmegaAssetStudio2.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/`.

## Cutting a release

```powershell
.\build\publish.ps1
```

Runs the tests, publishes self-contained x64 so a download needs no .NET runtime
or Windows App SDK installed, copies the licence notices in beside the binaries,
drops the debug symbols, and zips the result. Output goes to
`OmegaAssetStudio2_dist/` next to the repository, named for the version in
`Directory.Build.props`.

## What it can read

Everything below was derived from the game's own files, byte by byte, and is
verified against every installed client by the test suite.

- **Packages** — header, name table, import table, export table, object paths.
- **Compression** — LZO1X decompression in managed code, and the chunked block
  layout wrapping it.
- **Properties** — the tagged-property system, including structs, enums, names,
  and arrays of structures.
- **Textures** — dimensions, format, mip chains, and pixels, whether stored in
  the package or in a shared texture cache.
- **Materials** — the colour and numeric parameters a material instance
  overrides, the expressions a material is built from, and the constants its
  compiled shaders were baked with.
- **Skinned models** — bounds, skeleton, per-level sections, index buffers, and
  vertex positions, including the quantised form. Verified by decoding positions
  and checking every one lands inside bounds stored elsewhere in the file.
- **The game's data archive** — the prototype graph, the type and blueprint
  directories, and the localised strings, which together say what powers a
  character actually has rather than what a file name suggests.

## What it can write

Writes are deliberately narrow, because the failure mode is a corrupted game
install. There are two paths, and which one is used depends on whether the edit
changes any object's size.

**In place.** Where an edit fits the space it already occupies — a colour, a
parameter, a texture that encodes to the same size — the bytes are replaced
where they lie. Nothing else in the file moves.

**Rebuilt.** Where an edit cannot fit, the package is laid out afresh and every
offset in it rewritten. The result is **larger than the original and always
uncompressed**: the rebuilt file carries absolute positions, so compressing it
afterwards would move everything those positions point at and the game would
hang on the next load screen. The growth is the price of the edit, not an
oversight.

Either way:

- Every write takes a pristine backup first — once per file, never overwritten.
- Writes go to a temp file and swap in atomically, so an interrupted write leaves
  the original untouched.
- Packages shared by the whole game, or by characters other than the one being
  edited, are refused rather than written to.
- Textures whose pixels live in the shared cache are refused by the package
  writer rather than half-handled; the Icon Editor handles that cache itself.

## Repository layout

```
build/                          Build, run, and publish scripts
src/
  OmegaAssetStudio2.App/        WinUI 3 shell — the only project that knows about UI
    Rendering/                  The 3D viewport: device, camera, shaders
    Pages/                      One page per tool
  OmegaAssetStudio2.Core/       Engine — no UI dependency, referenced by tests
    Materials/                  Material parameters: read, edit, write
    Packages/                   Package format: header, tables, properties, writing
      Compression/              LZO1X and the chunked block layout
      Properties/               Tagged properties and struct arrays
    Meshes/                     Models, skeletons, geometry, and the cast list
    Textures/                   Formats, mips, cache, decode, encode, replace
    Workspace/                  Game clients, safe writes, path containment,
                                backups, and the index of what each file holds
  OmegaAssetStudio2.Calligraphy/   The game's own data archive: prototypes, types,
                                blueprints, localised strings, and the colour writer
  OmegaAssetStudio2.CharacterSwap/ Cross-version costume transplant
  OmegaAssetStudio2.RenderChecks/  Headless checks that the viewport still shades
                                what it did, run as part of the build
  UpkManager/                   Typed views over engine objects
  DDSLib/                       Texture encode and decode
tests/
  OmegaAssetStudio2.Core.Tests/ Mirrors the Core folder structure
vendor/                         Where a located vgmstream is looked for
```

Two rules keep this from drifting: **`Core` never references `App`**, and
**namespaces always match folders**.

## Tests

```powershell
.\build\build.ps1 -Test
```

The suite runs against real game installs when they are present and no-ops when
they are not, so it passes on any machine. Point it at your own with
`OAS2_CLIENT_ROOTS`, a semicolon-separated list of install folders.

The tests that matter most are the ones that prove a layout was read correctly
rather than merely plausibly: mip sizes that agree with format arithmetic,
manifest entries that chain end-to-end, and packages that re-read identically
after being rewritten.

## Licences

The application is MIT-licensed. It bundles **no GPL component** — version 1
shipped a GPL-licensed native LZO library, and the managed decompressor here
replaces it. See `THIRD_PARTY_NOTICES.txt` for the full notices.
