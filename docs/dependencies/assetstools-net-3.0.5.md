# AssetsTools.NET 3.0.5 Dependency Record

Reviewed: 2026-08-14

## Identity and provenance

- Package: `AssetsTools.NET` version `3.0.5`
- Official NuGet page: <https://www.nuget.org/packages/AssetsTools.NET/3.0.5>
- NuGet package download: <https://www.nuget.org/api/v2/package/AssetsTools.NET/3.0.5>
- Source repository declared by the package: <https://github.com/nesrak1/AssetsTools.NET>
- Source license: <https://github.com/nesrak1/AssetsTools.NET/blob/main/LICENSE>
- Package author/owner declared in the restored nuspec: `nesrak1`
- Package targets selected by S1Atlas: `.NETStandard 2.0`
- Restored package path: `%USERPROFILE%/.nuget/packages/assetstools.net/3.0.5/assetstools.net.3.0.5.nupkg`
- Restored `.nupkg` SHA-256: `e3b79ad8271aa8d84df541ddecba2402448408fd648b33b6a3c2e2a9a1c1d384`

The restored nuspec declares repository type `Git` and the repository URL above, but does not declare a source commit. The exact reviewed artifact is therefore identified by package ID/version plus the restored `.nupkg` SHA-256. `dotnet nuget verify --all` reports a valid NuGet.org repository signature with certificate SHA-256 `1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D` (valid 2024-02-22 through 2027-05-18).

## License and transitive inventory

| Component | Version | Relationship | License | Evidence |
| --- | --- | --- | --- | --- |
| AssetsTools.NET | 3.0.5 | Direct, `S1Atlas.Extraction` only | MIT | Restored nuspec SPDX expression `MIT`; source repository `LICENSE` |
| Transitive NuGet packages | None | None | Not applicable | `dotnet list ... package --include-transitive` lists only the direct package |

The MIT license permits use, modification, binary distribution, sublicensing, and sale. Distribution must retain the copyright and permission notice in copies or substantial portions. A V1 binary distribution must therefore include the AssetsTools.NET MIT notice in its third-party notices. The package archive does not contain a separate license file; the restored nuspec carries the SPDX expression and links to the standard MIT text, while the source repository contains the copyright notice.

As reviewed on 2026-08-14, `dotnet list src/S1Atlas.Extraction/S1Atlas.Extraction.csproj package --include-transitive --vulnerable` reported no known vulnerable packages from the configured NuGet.org source. This is a point-in-time result and must be rerun for a release or dependency update.

## S1Atlas use and isolation

The package is pinned only in `src/S1Atlas.Extraction/S1Atlas.Extraction.csproj`. Core, Storage, Indexing, and CLI have no direct package reference and expose no AssetsTools.NET type. `AssetsToolsUnitySerializedFileParser` is the only production source file that imports AssetsTools.NET namespaces; its public contract accepts and returns S1Atlas-owned records.

The adapter uses the package only to read Unity SerializedFile headers, object tables, class IDs, and external-file metadata. It does not invoke bundle extraction, texture, mesh, audio, shader, or payload-export APIs. It does not load Unity, the game, mods, managed game assemblies, or serialized code. Prefab evidence is based only on `PrefabInstance`/`Prefab` class IDs; object payload strings are not classification evidence.

Normal parsing is local, static, and offline. Network access occurs only during the explicit NuGet restore/acquisition and vulnerability-query operations, not while indexing scene inputs. The game installation remains read-only; parsing opens verified files with read access and never writes them.

## Acceptance decision

AssetsTools.NET 3.0.5 is acceptable for local S1Atlas use and planned V1 binary distribution because:

1. its permissive MIT terms are compatible with local use and redistribution when the notice is retained;
2. the selected package has no transitive NuGet dependency or additional license inventory;
3. the exact restored artifact is pinned, hashed, and repository-signed;
4. the S1Atlas adapter confines the dependency to Extraction and exposes no third-party types;
5. the implemented call surface is read-only SerializedFile metadata/object-table parsing and excludes runtime, managed-assembly, bundle-extraction, and asset-payload APIs; and
6. focused offline tests cover the target Unity 2022.3 SerializedFile version, object class IDs, external references, and false prefab-marker evidence.

Any version update requires a new artifact hash, signature and vulnerability verification, license/transitive review, adapter API review, sanitized fixture run, and real-install smoke before release acceptance.
