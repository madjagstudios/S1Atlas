# Unity class database (UABEA `classdata.tpk`) Dependency Record

Reviewed: 2026-09-19 (AT-47)

## Why S1Atlas needs it

Schedule I ships release builds whose SerializedFile containers strip their Unity
type trees (`TypeTreeEnabled=false` on all nine allowlisted containers, Unity
2022.3.62f2, serialized file version 22). AssetsTools.NET can decode an object only
from a type tree, so without another source the scene parser recovers no names,
hierarchy, or component attachments (AT-46 recorded this as
`SceneTypeTreeUnavailable`). A class database supplies the engine class layouts
(GameObject, Transform, RectTransform, MonoBehaviour, MonoScript, BuildSettings)
for a given Unity version and lets the parser decode those objects on real builds.

## Identity and provenance

- Package: `classdata.tpk`, the AssetsTools.NET class package (`ClassPackageFile`,
  magic `TPK*`, LZ4) shipped with UABEA
- Source repository: <https://github.com/nesrak1/UABEA> (author `nesrak1`, the
  AssetsTools.NET maintainer)
- Pinned commit: `5adb448deeefa1b88881f1fa44243009b352db3a` (2024-05-03, "misc updates";
  the last commit that touched `ReleaseFiles/classdata.tpk`)
- Pinned download: <https://raw.githubusercontent.com/nesrak1/UABEA/5adb448deeefa1b88881f1fa44243009b352db3a/ReleaseFiles/classdata.tpk>
- Git blob SHA-1: `a68aff1288d95bd6a58ec6ad99813ea9c59e0abc`
- Size: 289,605 bytes
- SHA-256: `129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6`
- Package creation time recorded inside the tpk: 2024-04-29T05:01:08Z
- Unity versions dumped: 1,008 (3.4 through 6000.0.0b16); the newest 2022.3 dump is
  `2022.3.26f1`

The tpk is not attached to a GitHub release; the UABEA v8 release zips (2024-11-03)
bundle the same file. The AssetsTools.NET repository documents the format
(`LoadClassPackage` / `LoadClassDatabaseFromPackage`) and points to release zips for
"updated class packages" but publishes no standalone tpk in its recent releases.

### Origin of the data

The package is generated with AssetRipper's `Tpk` tool
(<https://github.com/AssetRipper/Tpk>, MIT, copyright ds56789) from
`AssetRipper/TypeTreeDumps`, an archive of struct-layout dumps taken from official
Unity editor binaries with TypeTreeDumper. The dumps are structural metadata (class
IDs, field names, field types, alignment flags), not game content and not Unity
source. `TypeTreeDumps` carries no licence file and states only that it is not
affiliated with Unity Technologies.

## Licence and redistribution

| Component | Licence | Evidence |
| --- | --- | --- |
| UABEA repository, including `ReleaseFiles/classdata.tpk` | MIT (Copyright (c) 2021 nesrak1) | <https://github.com/nesrak1/UABEA/blob/5adb448deeefa1b88881f1fa44243009b352db3a/license> |
| AssetRipper/Tpk (generator) | MIT | GitHub licence API, SPDX `MIT` |
| AssetRipper/TypeTreeDumps (source dumps) | No licence file | GitHub licence API returns `null` |

Decision: **acceptable to use; not committed to this repository.** The file is
distributed by its author under the MIT licence of the UABEA repository and is
redistributed the same way by the MIT tools that read it (UABEA, AssetRipper,
AssetStudio). Because the underlying dump archive states no licence of its own,
S1Atlas does not add a second redistribution: the package is fetched from the
pinned commit by the existing managed-tool installer, verified by size and SHA-256,
and stored under the local tools root. This is the same posture as the Cpp2IL pin
(no third-party binary in the tree, every input hashed, provenance labelled) and it
keeps the MIT notice obligation with the upstream repository the user downloads
from.

## How it is pinned and used

- Definition: `config/tools/unity-classdata.win-x64.json` (tool ID `unity-classdata`,
  version `uabea-5adb448`, `package.kind = singleFile`, `probes = []`). The probe
  list is empty because the package is data, not an executable; the definition
  validator accepts an empty list and still requires the field.
- Install: `tools install unity-classdata` (the only network step). `tools status`
  reports `Verified` with `packageSha256 = executableSha256 = 129e1f80…`. Local path:
  `<data>/tools/unity-classdata/uabea-5adb448/classdata.tpk`.
- Use: `AssetsToolsUnitySerializedFileParser.ClassPackageSource` re-hashes the file
  against the pin before interpreting a byte, reads it once, and resolves a class
  database per container Unity version. A missing file leaves the parser without a
  fallback (`SceneTypeTreeUnavailable` names the install command); a hash mismatch
  is refused with a `--repair` hint. Parsing stays offline and read-only.
- Version selection: the package resolves every class by version range, so a
  request for `2022.3.62f2` succeeds, but the layouts actually used are those of the
  newest dump not newer than the container, `2022.3.26f1`. The snapshot provenance
  records this as
  `class-database unity-classdata uabea-5adb448 sha256:129e1f80… (2022.3.26f1 layouts for 2022.3.62f2; nearest earlier dump)`.
  An exact dump would be labelled `(… exact)`. The engine classes S1Atlas decodes
  did not change layout between 2022.3.26 and 2022.3.62 (verified end to end on build
  `0b86d6d8…`: 211,409 game objects, 589,372 components, no decode failure).
- Identity: the descriptor (tool ID, version, SHA-256) is part of the scene snapshot
  identity, so a different package produces a different snapshot rather than
  silently changing an existing one.

## Isolation

Only `src/S1Atlas.Extraction/Scene/AssetsToolsUnitySerializedFileParser.cs` reads the
package; the class-database source and resolution types are nested in that adapter
so no AssetsTools.NET type appears in any other signature (enforced by
`ParserIsolationTests`). Unit tests never load the real package: they build a
synthetic `ClassDatabaseFile` in memory from the sanitized fixture's own node lists
and exercise the missing-file, hash-mismatch, and unreadable-package paths with
generated bytes.

## Update procedure

A newer package (for example one that contains a `2022.3.62` dump) requires: a new
pinned commit or release URL, size and SHA-256, a rerun of the licence review above,
the `RepositoryToolDefinitionProviderTests` pin update, and a real-install smoke
(`index --scene --force`, `scenes`, `gameobject`) recorded in the changelog. The
version string in the definition changes the install root and the snapshot identity.
