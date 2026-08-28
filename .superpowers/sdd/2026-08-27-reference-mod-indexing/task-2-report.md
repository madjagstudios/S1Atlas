## 2026-08-28 Task 2 Report

Changed files:

- `src/S1Atlas.Indexing/ReferenceMods/ReferenceModManifestLoader.cs`
- `src/S1Atlas.Indexing/ReferenceMods/ReferenceModFileSelector.cs`
- `src/S1Atlas.Indexing/ReferenceMods/ReferenceModInputHasher.cs`
- `tests/S1Atlas.Indexing.Tests/ReferenceMods/ReferenceModManifestLoaderTests.cs`
- `tests/S1Atlas.Indexing.Tests/ReferenceMods/ReferenceModFileSelectorTests.cs`
- `.superpowers/sdd/2026-08-27-reference-mod-indexing/task-2-report.md`

Test-first evidence:

1. Initial red run after writing the new focused tests and before production code existed:

   Command:
   `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceMod"`

   Output excerpt:

   ```text
   tests\S1Atlas.Indexing.Tests\ReferenceMods\ReferenceModFileSelectorTests.cs(4,24): error CS0234: The type or namespace name 'ReferenceMods' does not exist in the namespace 'S1Atlas.Indexing'
   tests\S1Atlas.Indexing.Tests\ReferenceMods\ReferenceModManifestLoaderTests.cs(2,24): error CS0234: The type or namespace name 'ReferenceMods' does not exist in the namespace 'S1Atlas.Indexing'
   ```

2. Follow-up red run after the first implementation pass exposed behavioral gaps:

   Command:
   `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceMod"`

   Output excerpt:

   ```text
   Failed S1Atlas.Indexing.Tests.ReferenceMods.ReferenceModManifestLoaderTests.LoadAsync_normalizes_a_valid_qol_manifest
   System.IO.InvalidDataException : Reference mod manifest field 'mods[0].rootPath' must be a local filesystem path, not a URL.

   Failed S1Atlas.Indexing.Tests.ReferenceMods.ReferenceModFileSelectorTests.Select_and_hash_returns_sorted_safe_inputs_and_path_independent_collection_hash
   Assert.Equal() Failure: Collections differ
   ```

3. Additional red regression added during self-review to enforce metadata-sensitive hashing:

   Command:
   `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceModFileSelectorTests.HashAsync_changes_collection_hash_when_declared_mod_metadata_changes"`

   Output excerpt:

   ```text
   Assert.NotEqual() Failure: Strings are equal
   Expected: Not "286f9ac31ac97a99fcbe88f005fbb495760f5da2c8341a98f9..."
   ```

4. Additional red regression added during self-review to enforce strict unmapped-property rejection:

   Command:
   `dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceModManifestLoaderTests.LoadAsync_rejects_unmapped_json_properties"`

   Output excerpt:

   ```text
   Assert.Throws() Failure: No exception was thrown
   Expected: typeof(System.IO.InvalidDataException)
   ```

Implementation summary:

- Added a strict local manifest loader that:
  - normalizes collection and mod IDs to lower-case stable identifiers,
  - preserves display metadata,
  - canonicalizes and sorts include/exclude globs,
  - rejects missing roots, relative escapes, URLs, and unsupported JSON properties,
  - bounds manifest reads to local offline input.
- Added deterministic file selection that:
  - recursively selects only `.dll`, `.cs`, `.md`, `.markdown`, and `.txt`,
  - skips `bin`, `obj`, and cache trees through glob application,
  - refuses reparse-point traversal and out-of-root escapes,
  - returns ordinal `(modId, relativePath)` ordering.
- Added hashing that:
  - re-observes selected files before and after hashing,
  - fails on drift with `InvalidDataException`,
  - honors cancellation,
  - produces a collection content hash over selected file hashes plus declared mod metadata while excluding absolute paths.

Final verification:

Command:
`dotnet test tests/S1Atlas.Indexing.Tests/S1Atlas.Indexing.Tests.csproj --filter "FullyQualifiedName~ReferenceMod"`

Output:

```text
Passed!  - Failed:     0, Passed:    19, Skipped:     0, Total:    19, Duration: 140 ms - S1Atlas.Indexing.Tests.dll (net8.0)
```

Self-review:

- The implementation stays within Task 2: no indexing workflow, decompilation orchestration, CLI surface, or MCP surface was added.
- The collection hash is path-independent and now changes when declared mod metadata changes.
- The loader is strict about unknown JSON properties via explicit shape validation because the serializer option for unmapped-member rejection was not available in this target setup.
- I added a convenience overload `ReferenceModFileSelector.Select(IReadOnlyList<ReferenceModDefinition>)` for the focused tests; the required single-mod `Select(ReferenceModDefinition)` interface remains present.

Concerns:

- `license: "unknown"` is preserved as explicit local metadata, but Task 2’s required loader contract has no natural warning channel. A warning surface still needs to be threaded through the later workflow/CLI layer so `"unknown"` is not mistaken for permission.
