# AssetsTools.NET.MonoCecil 3.0.4 Dependency Record

Reviewed: 2026-09-24 (AT-50)

## Identity and provenance

- Package: `AssetsTools.NET.MonoCecil` version `3.0.4`
- Official NuGet page: <https://www.nuget.org/packages/AssetsTools.NET.MonoCecil/3.0.4>
- NuGet package download: <https://www.nuget.org/api/v2/package/AssetsTools.NET.MonoCecil/3.0.4>
- Source repository declared by the package: <https://github.com/nesrak1/AssetsTools.NET>
- Source license: <https://github.com/nesrak1/AssetsTools.NET/blob/main/LICENSE>
- Restored package path: `%USERPROFILE%/.nuget/packages/assetstools.net.monocecil/3.0.4/assetstools.net.monocecil.3.0.4.nupkg`
- Restored `.nupkg` SHA-256: `8568bb453c60719b64025147b260d98b33af9f946855d1cf43f176a5a5ab16ad`
- `dotnet nuget verify --all`: NuGet.org repository signature, certificate SHA-256 `1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D` (valid 2024-02-22 through 2027-05-18)

Transitive:

- Package: `Mono.Cecil` version `0.11.3` (resolved from the `>= 0.11.3` dependency of the `.NETStandard2.0` group)
- Official NuGet page: <https://www.nuget.org/packages/Mono.Cecil/0.11.3>
- Source repository declared by the package: <https://github.com/jbevain/cecil>
- Restored package path: `%USERPROFILE%/.nuget/packages/mono.cecil/0.11.3/mono.cecil.0.11.3.nupkg`
- Restored `.nupkg` SHA-256: `43125c46ddde632ed1d26736e45fe13399f8a95a8102564286c10a9fe63d9d75`
- `dotnet nuget verify --all`: NuGet.org repository signature, certificate SHA-256 `0E5F38F57DC1BCC806D8494F4F90FBCEDD988B46760709CBEEC6F4219AA6157D` (validity window 2018-04-09 through 2021-04-14; the repository signature is timestamped, so the package verifies)

Neither nuspec declares a source commit. The reviewed artifacts are identified by package ID/version plus the restored `.nupkg` SHA-256 values above.

## License and transitive inventory

| Component | Version | Relationship | License | Evidence |
| --- | --- | --- | --- | --- |
| AssetsTools.NET.MonoCecil | 3.0.4 | Direct, `S1Atlas.Extraction` only | MIT | Restored nuspec SPDX expression `MIT`; source repository `LICENSE` |
| Mono.Cecil | 0.11.3 | Transitive via AssetsTools.NET.MonoCecil | MIT | Restored nuspec `licenseUrl` points to the MIT license; source repository `LICENSE` |

Both are MIT: use, modification, binary distribution, sublicensing, and sale are permitted provided the copyright and permission notice is retained. A binary distribution must include both MIT notices in its third-party notices.

As reviewed on 2026-09-24, `dotnet list src/S1Atlas.Extraction/S1Atlas.Extraction.csproj package --include-transitive --vulnerable` reported no known vulnerable packages from NuGet.org. This is a point-in-time result and must be rerun for a release or dependency update.

## S1Atlas use and isolation

The package is pinned only in `src/S1Atlas.Extraction/S1Atlas.Extraction.csproj`. It is used only inside the AssetsTools.NET adapter (`src/S1Atlas.Extraction/Scene/AssetsToolsUnitySerializedFileParser*.cs`), whose public contract accepts and returns S1Atlas-owned records; `ParserIsolationTests` enforces that no AssetsTools.NET type (this package included) leaks into another file, project, or signature.

The adapter uses `MonoCecilTempGenerator` to build a MonoBehaviour field layout from a Cpp2IL-reconstructed `Assembly-CSharp.dll` in the preferred verified extraction. Mono.Cecil reads that assembly's metadata only; it is never loaded into the process, executed, or JIT-compiled, and no game code runs. Layouts are used only when the reconstruction carries restored `[SerializeField]` attributes, and each decoded object must consume exactly its serialized byte count.

Parsing stays local, static, and offline. Network access happens only during explicit NuGet restore and vulnerability queries.

## Acceptance decision

AssetsTools.NET.MonoCecil 3.0.4 (with Mono.Cecil 0.11.3) is acceptable for local use and planned binary distribution because both are MIT-licensed, the exact artifacts are pinned, hashed, and repository-signed, no known vulnerabilities are reported, the dependency is confined to the Extraction adapter, and its use is limited to reading reconstructed assembly metadata. Any version update requires a new hash, signature, vulnerability, license, and adapter API review plus a fixture run and real-install smoke before release acceptance.
