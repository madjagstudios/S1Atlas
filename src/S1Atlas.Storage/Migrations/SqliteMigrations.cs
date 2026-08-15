namespace S1Atlas.Storage.Migrations;

internal static class SqliteMigrations
{
    private const string FoundationV1Sql = """
        CREATE TABLE builds (
            build_id TEXT NOT NULL PRIMARY KEY,
            game_version TEXT NULL,
            steam_build_id TEXT NULL,
            game_assembly_sha256 TEXT NOT NULL,
            metadata_sha256 TEXT NOT NULL,
            scanned_at_utc TEXT NOT NULL,
            is_valid INTEGER NOT NULL CHECK (is_valid IN (0, 1))
        );

        CREATE TABLE environment_snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            atlas_version TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id)
        );

        CREATE INDEX ix_environment_snapshots_build_id
        ON environment_snapshots(build_id);

        CREATE TABLE dependencies (
            snapshot_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            kind TEXT NOT NULL,
            version TEXT NULL,
            path TEXT NULL,
            is_installed INTEGER NOT NULL CHECK (is_installed IN (0, 1)),
            PRIMARY KEY (snapshot_id, ordinal),
            FOREIGN KEY (snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
                ON DELETE CASCADE
        );

        CREATE INDEX ix_dependencies_snapshot_kind
        ON dependencies(snapshot_id, kind);

        CREATE TABLE atlas_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            current_snapshot_id TEXT NULL,
            FOREIGN KEY (current_snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
        );

        INSERT OR IGNORE INTO atlas_state (singleton_id, current_snapshot_id)
        VALUES (1, NULL);
        """;

    private const string EnvironmentObservationsV2Sql = """
        ALTER TABLE builds
        RENAME COLUMN scanned_at_utc TO first_seen_at_utc;

        ALTER TABLE environment_snapshots
        ADD COLUMN identity_version INTEGER NOT NULL DEFAULT 1
        CHECK (identity_version > 0);

        ALTER TABLE environment_snapshots
        ADD COLUMN executable_version TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN steam_app_id TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN steam_build_id TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN installation_root TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN game_assembly_path TEXT NULL;

        ALTER TABLE environment_snapshots
        ADD COLUMN global_metadata_path TEXT NULL;

        UPDATE environment_snapshots
        SET executable_version = (
                SELECT builds.game_version
                FROM builds
                WHERE builds.build_id = environment_snapshots.build_id),
            steam_build_id = (
                SELECT builds.steam_build_id
                FROM builds
                WHERE builds.build_id = environment_snapshots.build_id);

        ALTER TABLE builds DROP COLUMN game_version;
        ALTER TABLE builds DROP COLUMN steam_build_id;
        """;

    private const string ManagedToolsV3Sql = """
        CREATE TABLE managed_tool_installations (
            tool_id TEXT NOT NULL,
            version TEXT NOT NULL,
            platform TEXT NOT NULL,
            definition_digest TEXT NOT NULL,
            package_sha256 TEXT NOT NULL,
            executable_sha256 TEXT NOT NULL,
            root_path TEXT NOT NULL,
            status TEXT NOT NULL,
            installed_at_utc TEXT NOT NULL,
            last_verified_at_utc TEXT NOT NULL,
            probe_summary TEXT NOT NULL,
            PRIMARY KEY (tool_id, version, platform)
        );

        CREATE TABLE tool_instances (
            tool_instance_id TEXT NOT NULL PRIMARY KEY,
            tool_name TEXT NOT NULL,
            version_label TEXT NULL,
            platform TEXT NOT NULL,
            trust_level TEXT NOT NULL,
            definition_digest TEXT NULL,
            package_sha256 TEXT NULL,
            executable_sha256 TEXT NOT NULL,
            observed_path TEXT NOT NULL,
            first_observed_at_utc TEXT NOT NULL,
            last_verified_at_utc TEXT NOT NULL,
            status TEXT NOT NULL
        );

        CREATE INDEX ix_tool_instances_tool_trust
        ON tool_instances(tool_name, trust_level);
        """;

    private const string ExtractionAttemptsV4Sql = """
        CREATE TABLE input_snapshots (
            input_snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            root_path TEXT NOT NULL,
            manifest_digest TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            replay_verified INTEGER NOT NULL CHECK (replay_verified IN (0, 1)),
            replay_verified_at_utc TEXT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id)
        );

        CREATE TABLE input_snapshot_files (
            input_snapshot_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            role TEXT NOT NULL,
            size INTEGER NOT NULL CHECK (size >= 0),
            sha256 TEXT NOT NULL,
            PRIMARY KEY (input_snapshot_id, relative_path),
            FOREIGN KEY (input_snapshot_id)
                REFERENCES input_snapshots(input_snapshot_id)
                ON DELETE CASCADE
        );

        CREATE TABLE extraction_attempts (
            attempt_id TEXT NOT NULL PRIMARY KEY,
            recipe_id TEXT NULL,
            build_id TEXT NOT NULL,
            tool_instance_id TEXT NULL,
            profile_id TEXT NOT NULL,
            profile_version INTEGER NOT NULL,
            profile_digest TEXT NOT NULL,
            validation_policy_id TEXT NOT NULL,
            validation_policy_version INTEGER NOT NULL,
            validation_policy_digest TEXT NOT NULL,
            adapter_version INTEGER NOT NULL,
            extraction_schema_version INTEGER NOT NULL,
            input_source TEXT NULL,
            input_snapshot_id TEXT NULL,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            pre_input_manifest_digest TEXT NULL,
            post_input_manifest_digest TEXT NULL,
            working_path TEXT NOT NULL,
            stdout_path TEXT NOT NULL,
            stderr_path TEXT NOT NULL,
            stdout_truncated INTEGER NOT NULL CHECK (stdout_truncated IN (0, 1)),
            stderr_truncated INTEGER NOT NULL CHECK (stderr_truncated IN (0, 1)),
            stdout_discarded_bytes INTEGER NOT NULL CHECK (stdout_discarded_bytes >= 0),
            stderr_discarded_bytes INTEGER NOT NULL CHECK (stderr_discarded_bytes >= 0),
            process_id INTEGER NULL,
            process_exit_code INTEGER NULL,
            failure_stage TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            keep_failed_artifacts INTEGER NOT NULL CHECK (keep_failed_artifacts IN (0, 1)),
            discarded_file_count INTEGER NOT NULL CHECK (discarded_file_count >= 0),
            discarded_byte_count INTEGER NOT NULL CHECK (discarded_byte_count >= 0),
            candidate_output_path TEXT NULL,
            result_extraction_id TEXT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id),
            FOREIGN KEY (tool_instance_id) REFERENCES tool_instances(tool_instance_id),
            FOREIGN KEY (input_snapshot_id) REFERENCES input_snapshots(input_snapshot_id)
        );

        CREATE INDEX ix_extraction_attempts_build_created
        ON extraction_attempts(build_id, created_at_utc DESC);
        CREATE INDEX ix_extraction_attempts_recipe
        ON extraction_attempts(recipe_id);
        CREATE INDEX ix_extraction_attempts_status
        ON extraction_attempts(status);
        """;

    private const string ValidatedExtractionsV5Sql = """
        ALTER TABLE extraction_attempts
        ADD COLUMN validation_source_extraction_id TEXT NULL;

        CREATE TABLE validated_extractions (
            extraction_id TEXT NOT NULL PRIMARY KEY,
            recipe_id TEXT NOT NULL,
            build_id TEXT NOT NULL,
            tool_instance_id TEXT NOT NULL,
            source_attempt_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            profile_version INTEGER NOT NULL,
            profile_digest TEXT NOT NULL,
            adapter_version INTEGER NOT NULL,
            extraction_schema_version INTEGER NOT NULL,
            artifact_manifest_digest TEXT NOT NULL,
            root_path TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            trust_level TEXT NOT NULL,
            validation_outcome TEXT NOT NULL,
            artifact_count INTEGER NOT NULL CHECK (artifact_count >= 0),
            library_count INTEGER NOT NULL CHECK (library_count >= 0),
            managed_assembly_count INTEGER NOT NULL CHECK (managed_assembly_count >= 0),
            type_count INTEGER NOT NULL CHECK (type_count >= 0),
            method_count INTEGER NOT NULL CHECK (method_count >= 0),
            field_count INTEGER NOT NULL CHECK (field_count >= 0),
            property_count INTEGER NOT NULL CHECK (property_count >= 0),
            event_count INTEGER NOT NULL CHECK (event_count >= 0),
            total_output_bytes INTEGER NOT NULL CHECK (total_output_bytes >= 0),
            total_managed_bytes INTEGER NOT NULL CHECK (total_managed_bytes >= 0),
            FOREIGN KEY (build_id) REFERENCES builds(build_id),
            FOREIGN KEY (tool_instance_id) REFERENCES tool_instances(tool_instance_id),
            FOREIGN KEY (source_attempt_id) REFERENCES extraction_attempts(attempt_id)
        );

        CREATE UNIQUE INDEX ux_validated_extractions_recipe_manifest
        ON validated_extractions(recipe_id, artifact_manifest_digest);

        CREATE INDEX ix_validated_extractions_build_created
        ON validated_extractions(build_id, created_at_utc DESC);

        CREATE TABLE extraction_artifacts (
            extraction_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            kind TEXT NOT NULL,
            size INTEGER NOT NULL CHECK (size >= 0),
            sha256 TEXT NOT NULL,
            assembly_name TEXT NULL,
            module_name TEXT NULL,
            type_count INTEGER NULL CHECK (type_count IS NULL OR type_count >= 0),
            method_count INTEGER NULL CHECK (method_count IS NULL OR method_count >= 0),
            field_count INTEGER NULL CHECK (field_count IS NULL OR field_count >= 0),
            property_count INTEGER NULL CHECK (property_count IS NULL OR property_count >= 0),
            event_count INTEGER NULL CHECK (event_count IS NULL OR event_count >= 0),
            PRIMARY KEY (extraction_id, relative_path),
            FOREIGN KEY (extraction_id)
                REFERENCES validated_extractions(extraction_id)
                ON DELETE RESTRICT
        );

        CREATE INDEX ix_extraction_artifacts_assembly_name
        ON extraction_artifacts(assembly_name);

        CREATE TABLE extraction_validation_results (
            attempt_id TEXT NOT NULL PRIMARY KEY,
            subject_extraction_id TEXT NULL,
            artifact_manifest_digest TEXT NOT NULL,
            policy_id TEXT NOT NULL,
            policy_version INTEGER NOT NULL,
            policy_digest TEXT NOT NULL,
            outcome TEXT NOT NULL,
            report_path TEXT NOT NULL,
            baseline_extraction_id TEXT NULL,
            preference_eligible INTEGER NOT NULL CHECK (preference_eligible IN (0, 1)),
            validated_at_utc TEXT NOT NULL,
            artifact_count INTEGER NOT NULL CHECK (artifact_count >= 0),
            library_count INTEGER NOT NULL CHECK (library_count >= 0),
            managed_assembly_count INTEGER NOT NULL CHECK (managed_assembly_count >= 0),
            type_count INTEGER NOT NULL CHECK (type_count >= 0),
            method_count INTEGER NOT NULL CHECK (method_count >= 0),
            field_count INTEGER NOT NULL CHECK (field_count >= 0),
            property_count INTEGER NOT NULL CHECK (property_count >= 0),
            event_count INTEGER NOT NULL CHECK (event_count >= 0),
            total_output_bytes INTEGER NOT NULL CHECK (total_output_bytes >= 0),
            total_managed_bytes INTEGER NOT NULL CHECK (total_managed_bytes >= 0),
            FOREIGN KEY (attempt_id) REFERENCES extraction_attempts(attempt_id),
            FOREIGN KEY (subject_extraction_id) REFERENCES validated_extractions(extraction_id),
            FOREIGN KEY (baseline_extraction_id) REFERENCES validated_extractions(extraction_id)
        );

        CREATE INDEX ix_extraction_validation_subject_policy
        ON extraction_validation_results(subject_extraction_id, policy_digest, validated_at_utc DESC);

        CREATE TABLE extraction_validation_issues (
            attempt_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            severity TEXT NOT NULL,
            code TEXT NOT NULL,
            message TEXT NOT NULL,
            artifact_relative_path TEXT NULL,
            preference_blocking INTEGER NOT NULL CHECK (preference_blocking IN (0, 1)),
            PRIMARY KEY (attempt_id, ordinal),
            FOREIGN KEY (attempt_id)
                REFERENCES extraction_attempts(attempt_id)
                ON DELETE CASCADE
        );

        CREATE TABLE preferred_extractions (
            build_id TEXT NOT NULL PRIMARY KEY,
            extraction_id TEXT NOT NULL,
            selected_at_utc TEXT NOT NULL,
            selection_reason TEXT NOT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id),
            FOREIGN KEY (extraction_id) REFERENCES validated_extractions(extraction_id)
        );

        CREATE TABLE extraction_preference_events (
            event_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            previous_extraction_id TEXT NULL,
            new_extraction_id TEXT NULL,
            selected_at_utc TEXT NOT NULL,
            selection_reason TEXT NOT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id),
            FOREIGN KEY (previous_extraction_id) REFERENCES validated_extractions(extraction_id),
            FOREIGN KEY (new_extraction_id) REFERENCES validated_extractions(extraction_id)
        );

        CREATE INDEX ix_extraction_preference_events_build_time
        ON extraction_preference_events(build_id, selected_at_utc DESC);
        """;

    private const string IndexingV6Sql = """
        CREATE TABLE code_snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY,
            codebase TEXT NOT NULL CHECK (codebase IN ('ScheduleI', 'S1Api', 'S1MApi')),
            channel TEXT NOT NULL CHECK (channel IN ('Installed', 'Release', 'Preview')),
            environment_snapshot_id TEXT NULL,
            source_identity TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            CHECK (codebase <> 'ScheduleI' OR channel = 'Installed'),
            FOREIGN KEY (environment_snapshot_id)
                REFERENCES environment_snapshots(snapshot_id)
                ON DELETE SET NULL
        );

        CREATE INDEX ix_code_snapshots_lookup
        ON code_snapshots(codebase, channel, created_at_utc DESC);

        CREATE TABLE index_runs (
            index_id TEXT NOT NULL PRIMARY KEY,
            snapshot_id TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Running', 'Completed', 'Failed')),
            started_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NULL,
            failure_message TEXT NULL,
            FOREIGN KEY (snapshot_id) REFERENCES code_snapshots(snapshot_id)
        );

        CREATE INDEX ix_index_runs_completed
        ON index_runs(snapshot_id, status, completed_at_utc DESC);

        CREATE TABLE symbols (
            symbol_id TEXT NOT NULL PRIMARY KEY,
            snapshot_id TEXT NOT NULL,
            canonical_key TEXT NOT NULL,
            kind TEXT NOT NULL,
            qualified_name TEXT NOT NULL,
            signature TEXT NOT NULL,
            is_best_effort INTEGER NOT NULL CHECK (is_best_effort IN (0, 1)),
            FOREIGN KEY (snapshot_id) REFERENCES code_snapshots(snapshot_id)
        );

        CREATE UNIQUE INDEX ux_symbols_snapshot_key
        ON symbols(snapshot_id, canonical_key);

        CREATE TABLE source_files (
            source_file_id TEXT NOT NULL PRIMARY KEY,
            snapshot_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            byte_count INTEGER NOT NULL CHECK (byte_count >= 0),
            FOREIGN KEY (snapshot_id) REFERENCES code_snapshots(snapshot_id)
        );

        CREATE UNIQUE INDEX ux_source_files_snapshot_path
        ON source_files(snapshot_id, relative_path);

        CREATE TABLE source_locations (
            symbol_id TEXT NOT NULL PRIMARY KEY,
            source_file_id TEXT NOT NULL,
            start_line INTEGER NOT NULL CHECK (start_line > 0),
            start_column INTEGER NOT NULL CHECK (start_column > 0),
            end_line INTEGER NULL CHECK (end_line IS NULL OR end_line >= start_line),
            end_column INTEGER NULL CHECK (end_column IS NULL OR end_column > 0),
            FOREIGN KEY (symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
            FOREIGN KEY (source_file_id) REFERENCES source_files(source_file_id)
        );

        CREATE TABLE symbol_fingerprints (
            symbol_id TEXT NOT NULL,
            fingerprint_kind TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            PRIMARY KEY (symbol_id, fingerprint_kind),
            FOREIGN KEY (symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE
        );

        CREATE TABLE relationships (
            relationship_id TEXT NOT NULL PRIMARY KEY,
            snapshot_id TEXT NOT NULL,
            source_symbol_id TEXT NOT NULL,
            target_symbol_id TEXT NULL,
            target_text TEXT NULL,
            relationship_kind TEXT NOT NULL,
            evidence TEXT NOT NULL,
            FOREIGN KEY (snapshot_id) REFERENCES code_snapshots(snapshot_id),
            FOREIGN KEY (source_symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
            FOREIGN KEY (target_symbol_id) REFERENCES symbols(symbol_id)
        );

        CREATE INDEX ix_relationships_source_kind
        ON relationships(source_symbol_id, relationship_kind);

        CREATE INDEX ix_relationships_target_kind
        ON relationships(target_symbol_id, relationship_kind);

        CREATE TABLE upstream_repositories (
            repository_id TEXT NOT NULL PRIMARY KEY,
            codebase TEXT NOT NULL CHECK (codebase IN ('S1Api', 'S1MApi')),
            owner TEXT NOT NULL,
            name TEXT NOT NULL,
            default_branch TEXT NULL,
            UNIQUE (codebase, owner, name)
        );

        CREATE TABLE upstream_snapshots (
            snapshot_id TEXT NOT NULL PRIMARY KEY,
            repository_id TEXT NOT NULL,
            commit_sha TEXT NOT NULL,
            captured_at_utc TEXT NOT NULL,
            status TEXT NOT NULL,
            FOREIGN KEY (repository_id) REFERENCES upstream_repositories(repository_id),
            UNIQUE (repository_id, commit_sha)
        );

        CREATE TABLE upstream_state (
            repository_id TEXT NOT NULL PRIMARY KEY,
            latest_snapshot_id TEXT NULL,
            checked_at_utc TEXT NULL,
            stale_after_utc TEXT NULL,
            FOREIGN KEY (repository_id) REFERENCES upstream_repositories(repository_id),
            FOREIGN KEY (latest_snapshot_id) REFERENCES upstream_snapshots(snapshot_id)
        );
        """;

    private const string BodyRecoveryV7Sql = """
        ALTER TABLE symbols
        ADD COLUMN body_recovery_status TEXT NULL
        CHECK (
            body_recovery_status IS NULL OR
            body_recovery_status IN ('NoBodyByDesign', 'Recovered', 'StubOrUnavailable', 'Unknown')
        );
        """;

    private const string SceneIntelligenceV8Sql = """
        CREATE TABLE scene_snapshots (
            scene_snapshot_id TEXT NOT NULL PRIMARY KEY,
            build_id TEXT NOT NULL,
            extraction_id TEXT NOT NULL,
            input_snapshot_id TEXT NOT NULL,
            code_snapshot_id TEXT NOT NULL,
            code_index_id TEXT NOT NULL,
            parser_id TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            container_manifest_digest TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Running', 'Completed', 'Failed')),
            recovery_status TEXT NOT NULL CHECK (recovery_status IN ('FullyRecovered', 'PartiallyRecovered', 'GraphOnly', 'StubOrUnavailable', 'Unknown')),
            started_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NULL,
            failure_code TEXT NULL,
            failure_message TEXT NULL,
            FOREIGN KEY (build_id) REFERENCES builds(build_id),
            FOREIGN KEY (extraction_id) REFERENCES validated_extractions(extraction_id),
            FOREIGN KEY (input_snapshot_id) REFERENCES input_snapshots(input_snapshot_id),
            FOREIGN KEY (code_snapshot_id) REFERENCES code_snapshots(snapshot_id),
            FOREIGN KEY (code_index_id) REFERENCES index_runs(index_id)
        );

        CREATE INDEX ix_scene_snapshots_build_status_completed
        ON scene_snapshots(build_id, status, completed_at_utc);

        CREATE TABLE scene_containers (
            container_id TEXT NOT NULL PRIMARY KEY,
            scene_snapshot_id TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            container_kind TEXT NOT NULL,
            unity_version TEXT NOT NULL,
            serialized_file_version INTEGER NOT NULL CHECK (serialized_file_version >= 0),
            byte_count INTEGER NOT NULL CHECK (byte_count >= 0),
            sha256 TEXT NOT NULL,
            sidecar_manifest TEXT NOT NULL,
            FOREIGN KEY (scene_snapshot_id) REFERENCES scene_snapshots(scene_snapshot_id)
        );

        CREATE INDEX ix_scene_containers_snapshot_path
        ON scene_containers(scene_snapshot_id, relative_path);

        CREATE TABLE scenes (
            scene_id TEXT NOT NULL PRIMARY KEY,
            scene_snapshot_id TEXT NOT NULL,
            container_id TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('Scene', 'Prefab')),
            name TEXT NOT NULL,
            source_local_file_id INTEGER NULL,
            object_count INTEGER NOT NULL CHECK (object_count >= 0),
            root_count INTEGER NOT NULL CHECK (root_count >= 0),
            recovery_status TEXT NOT NULL CHECK (recovery_status IN ('FullyRecovered', 'PartiallyRecovered', 'GraphOnly', 'StubOrUnavailable', 'Unknown')),
            FOREIGN KEY (scene_snapshot_id) REFERENCES scene_snapshots(scene_snapshot_id),
            FOREIGN KEY (container_id) REFERENCES scene_containers(container_id)
        );

        CREATE INDEX ix_scenes_snapshot_kind_name
        ON scenes(scene_snapshot_id, kind, name);

        CREATE TABLE game_objects (
            game_object_id TEXT NOT NULL PRIMARY KEY,
            scene_id TEXT NOT NULL,
            scene_snapshot_id TEXT NOT NULL,
            container_id TEXT NOT NULL,
            local_file_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            active INTEGER NULL CHECK (active IS NULL OR active IN (0, 1)),
            layer INTEGER NULL,
            tag TEXT NULL,
            recovery_status TEXT NOT NULL CHECK (recovery_status IN ('FullyRecovered', 'PartiallyRecovered', 'GraphOnly', 'StubOrUnavailable', 'Unknown')),
            FOREIGN KEY (scene_id) REFERENCES scenes(scene_id),
            FOREIGN KEY (scene_snapshot_id) REFERENCES scene_snapshots(scene_snapshot_id),
            FOREIGN KEY (container_id) REFERENCES scene_containers(container_id)
        );

        CREATE INDEX ix_game_objects_scene_name
        ON game_objects(scene_id, name);
        CREATE INDEX ix_game_objects_snapshot_name
        ON game_objects(scene_snapshot_id, name);
        CREATE UNIQUE INDEX ux_game_objects_snapshot_container_local_file
        ON game_objects(scene_snapshot_id, container_id, local_file_id);

        CREATE TABLE transforms (
            game_object_id TEXT NOT NULL PRIMARY KEY,
            parent_game_object_id TEXT NULL,
            sibling_index INTEGER NULL CHECK (sibling_index IS NULL OR sibling_index >= 0),
            position_x REAL NULL,
            position_y REAL NULL,
            position_z REAL NULL,
            rotation_x REAL NULL,
            rotation_y REAL NULL,
            rotation_z REAL NULL,
            rotation_w REAL NULL,
            scale_x REAL NULL,
            scale_y REAL NULL,
            scale_z REAL NULL,
            recovery_status TEXT NOT NULL CHECK (recovery_status IN ('FullyRecovered', 'PartiallyRecovered', 'GraphOnly', 'StubOrUnavailable', 'Unknown')),
            FOREIGN KEY (game_object_id) REFERENCES game_objects(game_object_id),
            FOREIGN KEY (parent_game_object_id) REFERENCES game_objects(game_object_id)
        );

        CREATE INDEX ix_transforms_parent_game_object
        ON transforms(parent_game_object_id);

        CREATE TABLE components (
            component_id TEXT NOT NULL PRIMARY KEY,
            game_object_id TEXT NOT NULL,
            container_id TEXT NOT NULL,
            local_file_id INTEGER NOT NULL,
            unity_class_id INTEGER NOT NULL,
            kind TEXT NOT NULL,
            script_assembly TEXT NULL,
            script_namespace TEXT NULL,
            script_class TEXT NULL,
            resolved_type_symbol_id TEXT NULL,
            resolved_code_index_id TEXT NULL,
            type_resolution_status TEXT NOT NULL CHECK (type_resolution_status IN ('Resolved', 'UnresolvedText', 'Ambiguous', 'NotIndexed', 'Unavailable')),
            recovery_status TEXT NOT NULL CHECK (recovery_status IN ('FullyRecovered', 'PartiallyRecovered', 'GraphOnly', 'StubOrUnavailable', 'Unknown')),
            FOREIGN KEY (game_object_id) REFERENCES game_objects(game_object_id),
            FOREIGN KEY (container_id) REFERENCES scene_containers(container_id),
            FOREIGN KEY (resolved_type_symbol_id) REFERENCES symbols(symbol_id),
            FOREIGN KEY (resolved_code_index_id) REFERENCES index_runs(index_id)
        );

        CREATE INDEX ix_components_game_object_kind
        ON components(game_object_id, kind);
        CREATE INDEX ix_components_resolved_type_symbol
        ON components(resolved_type_symbol_id);

        CREATE TABLE serialized_refs (
            reference_id TEXT NOT NULL PRIMARY KEY,
            scene_snapshot_id TEXT NOT NULL,
            source_component_id TEXT NULL,
            field_path TEXT NULL,
            declared_type TEXT NULL,
            source_container_id TEXT NOT NULL,
            source_local_file_id INTEGER NOT NULL,
            target_container_id TEXT NULL,
            target_local_file_id INTEGER NULL,
            target_game_object_id TEXT NULL,
            target_component_id TEXT NULL,
            target_symbol_id TEXT NULL,
            target_text TEXT NULL,
            resolution_status TEXT NOT NULL CHECK (resolution_status IN ('Resolved', 'UnresolvedText', 'Ambiguous', 'NotIndexed', 'Unavailable')),
            evidence TEXT NOT NULL,
            recovery_status TEXT NOT NULL CHECK (recovery_status IN ('FullyRecovered', 'PartiallyRecovered', 'GraphOnly', 'StubOrUnavailable', 'Unknown')),
            FOREIGN KEY (scene_snapshot_id) REFERENCES scene_snapshots(scene_snapshot_id),
            FOREIGN KEY (source_component_id) REFERENCES components(component_id),
            FOREIGN KEY (source_container_id) REFERENCES scene_containers(container_id),
            FOREIGN KEY (target_container_id) REFERENCES scene_containers(container_id),
            FOREIGN KEY (target_game_object_id) REFERENCES game_objects(game_object_id),
            FOREIGN KEY (target_component_id) REFERENCES components(component_id),
            FOREIGN KEY (target_symbol_id) REFERENCES symbols(symbol_id)
        );

        CREATE INDEX ix_serialized_refs_source_field_path
        ON serialized_refs(source_component_id, field_path);
        CREATE INDEX ix_serialized_refs_target_game_object
        ON serialized_refs(target_game_object_id);
        CREATE INDEX ix_serialized_refs_target_symbol
        ON serialized_refs(target_symbol_id);
        """;

    private const string ScenePublicationV9Sql = """
        ALTER TABLE scene_snapshots
        ADD COLUMN published_at_utc TEXT NULL;

        CREATE INDEX ix_scene_snapshots_publication
        ON scene_snapshots(status, published_at_utc);
        """;

    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(1, "foundation-v1", FoundationV1Sql),
        new(2, "environment-observations-v2", EnvironmentObservationsV2Sql),
        new(3, "managed-tools-v3", ManagedToolsV3Sql),
        new(4, "extraction-attempts-v4", ExtractionAttemptsV4Sql),
        new(5, "validated-extractions-v5", ValidatedExtractionsV5Sql),
        new(6, "indexing-v6", IndexingV6Sql),
        new(7, "body-recovery-v7", BodyRecoveryV7Sql),
        new(8, "scene-intelligence-v8", SceneIntelligenceV8Sql),
        new(9, "scene-publication-v9", ScenePublicationV9Sql)
    ];
}
