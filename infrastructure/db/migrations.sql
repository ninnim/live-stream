CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE live_sessions (
        id uuid NOT NULL,
        workspace_id uuid NOT NULL,
        created_by_user_id uuid NOT NULL,
        title character varying(200) NOT NULL,
        description character varying(2000),
        status character varying(32) NOT NULL,
        visibility character varying(32) NOT NULL,
        recording_enabled boolean NOT NULL,
        media_path_name character varying(64) NOT NULL,
        started_at timestamp with time zone,
        ended_at timestamp with time zone,
        state_entered_at timestamp with time zone NOT NULL,
        last_error_code character varying(64),
        last_error_message character varying(500),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        version integer NOT NULL,
        CONSTRAINT pk_live_sessions PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE users (
        id uuid NOT NULL,
        email character varying(320) NOT NULL,
        display_name character varying(100) NOT NULL,
        password_hash character varying(512) NOT NULL,
        status character varying(32) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_users PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE workspaces (
        id uuid NOT NULL,
        name character varying(150) NOT NULL,
        owner_user_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_workspaces PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE ingest_credentials (
        id uuid NOT NULL,
        live_session_id uuid NOT NULL,
        issued_to_user_id uuid NOT NULL,
        token_hash character varying(64) NOT NULL,
        media_path_name character varying(64) NOT NULL,
        scope character varying(16) NOT NULL,
        issued_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        first_used_at timestamp with time zone,
        revoked_at timestamp with time zone,
        CONSTRAINT pk_ingest_credentials PRIMARY KEY (id),
        CONSTRAINT fk_ingest_credentials_live_sessions_live_session_id FOREIGN KEY (live_session_id) REFERENCES live_sessions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE live_session_events (
        id uuid NOT NULL,
        live_session_id uuid NOT NULL,
        type character varying(48) NOT NULL,
        from_status character varying(32),
        to_status character varying(32),
        error_code character varying(64),
        detail character varying(1000),
        correlation_id character varying(64),
        actor_user_id uuid,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_live_session_events PRIMARY KEY (id),
        CONSTRAINT fk_live_session_events_live_sessions_live_session_id FOREIGN KEY (live_session_id) REFERENCES live_sessions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE live_session_health (
        live_session_id uuid NOT NULL,
        status character varying(32) NOT NULL,
        ingest_connected boolean NOT NULL,
        bitrate_kbps integer,
        video_fps integer,
        viewer_count integer NOT NULL,
        bytes_received bigint NOT NULL,
        reconnect_count integer NOT NULL,
        last_ingest_at timestamp with time zone,
        recovery_started_at timestamp with time zone,
        observed_at timestamp with time zone,
        last_error_code character varying(64),
        CONSTRAINT pk_live_session_health PRIMARY KEY (live_session_id),
        CONSTRAINT fk_live_session_health_live_sessions_live_session_id FOREIGN KEY (live_session_id) REFERENCES live_sessions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE recordings (
        id uuid NOT NULL,
        live_session_id uuid NOT NULL,
        storage_key character varying(512) NOT NULL,
        status character varying(32) NOT NULL,
        media_format character varying(32) NOT NULL,
        duration_seconds integer,
        size_bytes bigint,
        segment_count integer NOT NULL,
        started_at timestamp with time zone,
        ended_at timestamp with time zone,
        failure_reason character varying(500),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_recordings PRIMARY KEY (id),
        CONSTRAINT fk_recordings_live_sessions_live_session_id FOREIGN KEY (live_session_id) REFERENCES live_sessions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE refresh_tokens (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash character varying(64) NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        revoked_at timestamp with time zone,
        replaced_by_token_id uuid,
        CONSTRAINT pk_refresh_tokens PRIMARY KEY (id),
        CONSTRAINT fk_refresh_tokens_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE TABLE workspace_members (
        id uuid NOT NULL,
        workspace_id uuid NOT NULL,
        user_id uuid NOT NULL,
        role character varying(32) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_workspace_members PRIMARY KEY (id),
        CONSTRAINT fk_workspace_members_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE,
        CONSTRAINT fk_workspace_members_workspaces_workspace_id FOREIGN KEY (workspace_id) REFERENCES workspaces (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_ingest_credentials_session_expires ON ingest_credentials (live_session_id, expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE UNIQUE INDEX ix_ingest_credentials_token_hash ON ingest_credentials (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_live_session_events_session_created ON live_session_events (live_session_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE UNIQUE INDEX ix_live_sessions_media_path ON live_sessions (media_path_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_live_sessions_status ON live_sessions (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_live_sessions_workspace_status_created ON live_sessions (workspace_id, status, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_recordings_session_status ON recordings (live_session_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE UNIQUE INDEX ix_refresh_tokens_token_hash ON refresh_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_refresh_tokens_user_expires ON refresh_tokens (user_id, expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE UNIQUE INDEX ix_users_email ON users (email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_workspace_members_user_id ON workspace_members (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE UNIQUE INDEX ix_workspace_members_workspace_user ON workspace_members (workspace_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    CREATE INDEX ix_workspaces_owner_user_id ON workspaces (owner_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830115706_InitialPhase1') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260830115706_InitialPhase1', '9.0.3');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE TABLE provider_accounts (
        id uuid NOT NULL,
        workspace_id uuid NOT NULL,
        provider character varying(32) NOT NULL,
        external_account_id character varying(200) NOT NULL,
        display_name character varying(200) NOT NULL,
        access_token_cipher character varying(8192) NOT NULL,
        refresh_token_cipher character varying(8192),
        access_token_expires_at timestamp with time zone,
        scopes character varying(1000) NOT NULL,
        status character varying(32) NOT NULL,
        last_error_code character varying(64),
        last_error_message character varying(500),
        linked_by_user_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        last_refreshed_at timestamp with time zone,
        CONSTRAINT pk_provider_accounts PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE TABLE stream_destinations (
        id uuid NOT NULL,
        live_session_id uuid NOT NULL,
        provider character varying(32) NOT NULL,
        display_name character varying(120) NOT NULL,
        credential_mode character varying(32) NOT NULL,
        ingest_url character varying(1000),
        stream_key_cipher character varying(2048),
        provider_account_id uuid,
        enabled boolean NOT NULL,
        status character varying(32) NOT NULL,
        last_error_code character varying(64),
        last_error_message character varying(500),
        resolved_ingest_url character varying(1000),
        resolved_stream_key_cipher character varying(2048),
        external_broadcast_id character varying(200),
        watch_url character varying(1000),
        attempt_count integer NOT NULL,
        next_retry_at timestamp with time zone,
        started_at timestamp with time zone,
        stopped_at timestamp with time zone,
        last_connected_at timestamp with time zone,
        bytes_sent bigint NOT NULL,
        state_entered_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        version integer NOT NULL,
        CONSTRAINT pk_stream_destinations PRIMARY KEY (id),
        CONSTRAINT fk_stream_destinations_live_sessions_live_session_id FOREIGN KEY (live_session_id) REFERENCES live_sessions (id) ON DELETE CASCADE,
        CONSTRAINT fk_stream_destinations_provider_accounts_provider_account_id FOREIGN KEY (provider_account_id) REFERENCES provider_accounts (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE TABLE destination_events (
        id uuid NOT NULL,
        stream_destination_id uuid NOT NULL,
        type character varying(48) NOT NULL,
        from_status character varying(32),
        to_status character varying(32),
        error_code character varying(64),
        detail character varying(1000),
        correlation_id character varying(64),
        actor_user_id uuid,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_destination_events PRIMARY KEY (id),
        CONSTRAINT fk_destination_events_stream_destinations_stream_destination_id FOREIGN KEY (stream_destination_id) REFERENCES stream_destinations (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE INDEX ix_destination_events_destination_created ON destination_events (stream_destination_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE UNIQUE INDEX ix_provider_accounts_workspace_provider_external ON provider_accounts (workspace_id, provider, external_account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE INDEX ix_stream_destinations_provider_account_id ON stream_destinations (provider_account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE INDEX ix_stream_destinations_session_created ON stream_destinations (live_session_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    CREATE INDEX ix_stream_destinations_status ON stream_destinations (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260830141452_Phase2Distribution') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260830141452_Phase2Distribution', '9.0.3');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904141114_Phase2DestinationByteBaseline') THEN
    ALTER TABLE stream_destinations ADD bytes_sent_baseline bigint NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904141114_Phase2DestinationByteBaseline') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260904141114_Phase2DestinationByteBaseline', '9.0.3');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE TABLE session_sources (
        id uuid NOT NULL,
        live_session_id uuid NOT NULL,
        role character varying(32) NOT NULL,
        display_name character varying(80) NOT NULL,
        media_path_name character varying(64) NOT NULL,
        status character varying(32) NOT NULL,
        pairing_code_hash character varying(64),
        pairing_code_expires_at timestamp with time zone,
        device_token_hash character varying(64),
        device_token_expires_at timestamp with time zone,
        is_program boolean NOT NULL,
        ingest_connected boolean NOT NULL,
        bitrate_kbps integer,
        bytes_received bigint NOT NULL,
        last_seen_at timestamp with time zone,
        paired_at timestamp with time zone,
        revoked_at timestamp with time zone,
        revoked_by_user_id uuid,
        created_by_user_id uuid NOT NULL,
        state_entered_at timestamp with time zone NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        version integer NOT NULL,
        CONSTRAINT pk_session_sources PRIMARY KEY (id),
        CONSTRAINT fk_session_sources_live_sessions_live_session_id FOREIGN KEY (live_session_id) REFERENCES live_sessions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE TABLE source_events (
        id uuid NOT NULL,
        session_source_id uuid NOT NULL,
        type character varying(48) NOT NULL,
        from_status character varying(32),
        to_status character varying(32),
        error_code character varying(64),
        detail character varying(1000),
        correlation_id character varying(64),
        actor_user_id uuid,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_source_events PRIMARY KEY (id),
        CONSTRAINT fk_source_events_session_sources_session_source_id FOREIGN KEY (session_source_id) REFERENCES session_sources (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE UNIQUE INDEX ix_session_sources_device_token_hash ON session_sources (device_token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE INDEX ix_session_sources_media_path ON session_sources (media_path_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE UNIQUE INDEX ix_session_sources_pairing_code_hash ON session_sources (pairing_code_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE INDEX ix_session_sources_session_created ON session_sources (live_session_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE INDEX ix_session_sources_status ON session_sources (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    CREATE INDEX ix_source_events_source_created ON source_events (session_source_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260904152804_Phase4MultiDevice') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260904152804_Phase4MultiDevice', '9.0.3');
    END IF;
END $EF$;
COMMIT;

