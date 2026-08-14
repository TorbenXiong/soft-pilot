//! `SQLite` state storage, schema migration, and transaction boundaries for one workspace host.

use std::{
    fs, io,
    path::{Path, PathBuf},
    time::Duration,
};

use rusqlite::{Connection, OpenFlags, OptionalExtension, Transaction, TransactionBehavior};
use softpilot_core::{HostTriple, WorkspaceId, WorkspaceMetadata};
use thiserror::Error;

const APPLICATION_ID: i64 = 0x5350_5431;
const BUSY_TIMEOUT: Duration = Duration::from_secs(5);

const MIGRATIONS: &[Migration] = &[
    Migration {
        version: 1,
        name: "initial-host-state",
        sql: r"
        CREATE TABLE schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            applied_at_unix_seconds INTEGER NOT NULL
                CHECK (applied_at_unix_seconds >= 0)
        ) STRICT;

        CREATE TABLE host_identity (
            singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
            workspace_id TEXT NOT NULL,
            host_triple TEXT NOT NULL,
            workspace_created_at_unix_seconds INTEGER NOT NULL
                CHECK (workspace_created_at_unix_seconds >= 0)
        ) STRICT;
    ",
    },
    Migration {
        version: 2,
        name: "installed-plugin-packages",
        sql: r"
        CREATE TABLE plugin_packages (
            plugin_id TEXT NOT NULL,
            version TEXT NOT NULL,
            package_sha256 TEXT NOT NULL CHECK (length(package_sha256) = 64),
            package_size_bytes INTEGER NOT NULL CHECK (package_size_bytes >= 0),
            relative_path TEXT NOT NULL,
            manifest_json TEXT NOT NULL,
            component_validated INTEGER NOT NULL CHECK (component_validated IN (0, 1)),
            installed_at_unix_seconds INTEGER NOT NULL
                CHECK (installed_at_unix_seconds >= 0),
            PRIMARY KEY (plugin_id, version)
        ) STRICT;

        CREATE INDEX plugin_packages_installed
            ON plugin_packages (plugin_id, installed_at_unix_seconds DESC);
    ",
    },
    Migration {
        version: 3,
        name: "active-plugin-versions",
        sql: r"
        CREATE TABLE active_plugins (
            plugin_id TEXT PRIMARY KEY,
            version TEXT NOT NULL,
            enabled_at_unix_seconds INTEGER NOT NULL
                CHECK (enabled_at_unix_seconds >= 0),
            FOREIGN KEY (plugin_id, version)
                REFERENCES plugin_packages (plugin_id, version)
                ON UPDATE RESTRICT ON DELETE RESTRICT
        ) STRICT;
    ",
    },
    Migration {
        version: 4,
        name: "trashed-plugin-packages",
        sql: r"
        CREATE TABLE trashed_plugin_packages (
            trash_id TEXT PRIMARY KEY,
            plugin_id TEXT NOT NULL,
            version TEXT NOT NULL,
            package_sha256 TEXT NOT NULL CHECK (length(package_sha256) = 64),
            package_size_bytes INTEGER NOT NULL CHECK (package_size_bytes >= 0),
            original_relative_path TEXT NOT NULL,
            trash_relative_path TEXT NOT NULL UNIQUE,
            manifest_json TEXT NOT NULL,
            component_validated INTEGER NOT NULL CHECK (component_validated IN (0, 1)),
            installed_at_unix_seconds INTEGER NOT NULL
                CHECK (installed_at_unix_seconds >= 0),
            trashed_at_unix_seconds INTEGER NOT NULL
                CHECK (trashed_at_unix_seconds >= 0),
            UNIQUE (plugin_id, version)
        ) STRICT;

        CREATE INDEX trashed_plugin_packages_time
            ON trashed_plugin_packages (trashed_at_unix_seconds, trash_id);
    ",
    },
    Migration {
        version: 5,
        name: "plugin-file-operation-journal",
        sql: r"
        CREATE TABLE plugin_file_operations (
            operation_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL CHECK (kind IN ('install', 'trash', 'restore')),
            plugin_id TEXT NOT NULL,
            version TEXT NOT NULL,
            package_sha256 TEXT NOT NULL CHECK (length(package_sha256) = 64),
            package_size_bytes INTEGER NOT NULL CHECK (package_size_bytes >= 0),
            original_relative_path TEXT NOT NULL,
            manifest_json TEXT NOT NULL,
            component_validated INTEGER NOT NULL CHECK (component_validated IN (0, 1)),
            installed_at_unix_seconds INTEGER NOT NULL
                CHECK (installed_at_unix_seconds >= 0),
            source_relative_directory TEXT NOT NULL,
            destination_relative_directory TEXT NOT NULL,
            trash_id TEXT,
            trash_relative_path TEXT,
            operation_at_unix_seconds INTEGER NOT NULL
                CHECK (operation_at_unix_seconds >= 0),
            CHECK (source_relative_directory <> destination_relative_directory),
            CHECK ((kind = 'install' AND trash_id IS NULL AND trash_relative_path IS NULL)
                OR (kind IN ('trash', 'restore')
                    AND trash_id IS NOT NULL AND trash_relative_path IS NOT NULL))
        ) STRICT;
    ",
    },
];

/// Current schema version understood by this build.
pub const CURRENT_SCHEMA_VERSION: u32 = 5;

/// Plugin package state persisted after an atomic filesystem commit.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledPluginPackage {
    /// Stable plugin identifier.
    pub plugin_id: String,
    /// Exact plugin version.
    pub version: String,
    /// Complete package SHA-256.
    pub package_sha256: String,
    /// Complete package byte length.
    pub package_size_bytes: u64,
    /// Package path relative to the workspace root.
    pub relative_path: String,
    /// Validated manifest serialized as JSON.
    pub manifest_json: String,
    /// Whether a declared Component passed static type validation.
    pub component_validated: bool,
    /// Installation time in Unix seconds.
    pub installed_at_unix_seconds: u64,
}

/// One installed package paired with its current activation state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InstalledPluginPackageState {
    /// Immutable installed package record.
    pub package: InstalledPluginPackage,
    /// Whether this exact plugin ID/version is active.
    pub active: bool,
}

/// Recoverable plugin package stored under the workspace trash directory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TrashedPluginPackage {
    /// Unique trash entry ID used as the owned directory name.
    pub trash_id: String,
    /// Immutable package metadata retained for restoration.
    pub package: InstalledPluginPackage,
    /// Current package path relative to the workspace root.
    pub trash_relative_path: String,
    /// Time the package was moved to trash, in Unix seconds.
    pub trashed_at_unix_seconds: u64,
}

/// Filesystem transition recorded before a plugin directory rename.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PluginFileOperationKind {
    /// Staging directory to immutable installed directory.
    Install,
    /// Immutable installed directory to recoverable trash.
    Trash,
    /// Recoverable trash directory to immutable installed directory.
    Restore,
}

impl PluginFileOperationKind {
    const fn as_str(self) -> &'static str {
        match self {
            Self::Install => "install",
            Self::Trash => "trash",
            Self::Restore => "restore",
        }
    }
}

impl TryFrom<&str> for PluginFileOperationKind {
    type Error = StorageError;

    fn try_from(value: &str) -> Result<Self, Self::Error> {
        match value {
            "install" => Ok(Self::Install),
            "trash" => Ok(Self::Trash),
            "restore" => Ok(Self::Restore),
            _ => Err(StorageError::InvalidPluginFileOperationKind(
                value.to_owned(),
            )),
        }
    }
}

/// Durable journal record for reconciling one plugin directory rename with `SQLite` state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PendingPluginFileOperation {
    /// Unique operation identifier.
    pub operation_id: String,
    /// Lifecycle transition kind.
    pub kind: PluginFileOperationKind,
    /// Immutable package state being moved.
    pub package: InstalledPluginPackage,
    /// Directory path before rename, relative to the workspace root.
    pub source_relative_directory: String,
    /// Directory path after rename, relative to the workspace root.
    pub destination_relative_directory: String,
    /// Trash entry ID for trash and restore transitions.
    pub trash_id: Option<String>,
    /// Package path under trash for trash and restore transitions.
    pub trash_relative_path: Option<String>,
    /// Time the operation journal was created, in Unix seconds.
    pub operation_at_unix_seconds: u64,
}

/// Result of idempotently recording a committed plugin package.
#[cfg(test)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum InsertPluginPackageOutcome {
    /// A new plugin version row was inserted.
    Inserted,
    /// The same immutable package was already recorded.
    AlreadyPresent,
}

/// Result of selecting an installed plugin version as active.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ActivatePluginOutcome {
    /// The active version was created or changed.
    Activated,
    /// The requested version was already active.
    AlreadyActive,
}

/// Result of disabling a plugin.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DisablePluginOutcome {
    /// The active version row was removed.
    Disabled,
    /// The plugin was already disabled.
    AlreadyDisabled,
}

/// Persistent identity that binds a host database to its workspace and target directory.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StateDatabaseIdentity {
    /// Stable workspace UUID from `workspace.json`.
    pub workspace_id: WorkspaceId,
    /// Host-specific directory owning this database.
    pub host_triple: HostTriple,
    /// Workspace creation time from `workspace.json`.
    pub workspace_created_at_unix_seconds: u64,
}

impl StateDatabaseIdentity {
    /// Creates a database identity from validated workspace metadata and a host triple.
    #[must_use]
    pub const fn new(metadata: WorkspaceMetadata, host_triple: HostTriple) -> Self {
        Self {
            workspace_id: metadata.workspace_id,
            host_triple,
            workspace_created_at_unix_seconds: metadata.created_at_unix_seconds,
        }
    }
}

/// Open `SQLite` database with a validated and current `SoftPilot` schema.
#[derive(Debug)]
pub struct StateDatabase {
    connection: Connection,
    path: PathBuf,
}

impl StateDatabase {
    /// Opens or initializes one host's state database.
    ///
    /// New databases and migrations are committed atomically. A database belonging to another
    /// application, workspace, or host is rejected before `SoftPilot` state is written.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] for unsafe paths, `SQLite` failures, unsupported schema versions,
    /// incomplete migration history, or identity mismatches.
    pub fn open(
        path: impl AsRef<Path>,
        identity: StateDatabaseIdentity,
    ) -> Result<Self, StorageError> {
        Self::open_with_migrations(path.as_ref(), identity, MIGRATIONS)
    }

    fn open_with_migrations(
        path: &Path,
        identity: StateDatabaseIdentity,
        migrations: &[Migration],
    ) -> Result<Self, StorageError> {
        validate_migrations(migrations)?;
        validate_database_path(path)?;

        let mut connection = Connection::open_with_flags(
            path,
            OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_CREATE,
        )?;
        connection.busy_timeout(BUSY_TIMEOUT)?;
        connection.pragma_update(None, "foreign_keys", "ON")?;
        connection.pragma_update(None, "trusted_schema", "OFF")?;

        {
            let transaction =
                connection.transaction_with_behavior(TransactionBehavior::Immediate)?;
            migrate_and_validate(&transaction, identity, migrations)?;
            transaction.commit()?;
        }

        connection.pragma_update(None, "synchronous", "FULL")?;
        let journal_mode: String =
            connection.query_row("PRAGMA journal_mode = WAL", [], |row| row.get(0))?;
        if !journal_mode.eq_ignore_ascii_case("wal") {
            return Err(StorageError::JournalMode(journal_mode));
        }

        Ok(Self {
            connection,
            path: path.to_owned(),
        })
    }

    /// Returns the database file path.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Returns the schema version validated when this database was opened.
    #[must_use]
    pub const fn schema_version(&self) -> u32 {
        CURRENT_SCHEMA_VERSION
    }

    /// Runs an immediate `SQLite` transaction and commits only when the operation succeeds.
    ///
    /// The workspace lock remains the outer cross-process write boundary. This method supplies
    /// the inner database rollback boundary for a single state change.
    ///
    /// # Errors
    ///
    /// Returns [`TransactionError::Storage`] when beginning or committing the transaction fails.
    /// An [`TransactionError::Operation`] rolls the transaction back when it is dropped.
    pub fn transaction<T, E>(
        &mut self,
        operation: impl FnOnce(&Transaction<'_>) -> Result<T, E>,
    ) -> Result<T, TransactionError<E>> {
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(StorageError::from)
            .map_err(TransactionError::Storage)?;
        let result = operation(&transaction).map_err(TransactionError::Operation)?;
        transaction
            .commit()
            .map_err(StorageError::from)
            .map_err(TransactionError::Storage)?;
        Ok(result)
    }

    /// Reads one installed plugin version.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails or stored integer fields are invalid.
    pub fn plugin_package(
        &self,
        plugin_id: &str,
        version: &str,
    ) -> Result<Option<InstalledPluginPackage>, StorageError> {
        load_plugin_package(&self.connection, plugin_id, version)
    }

    /// Lists every installed immutable plugin package in stable storage order.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails or stored integer fields are invalid.
    #[cfg(test)]
    fn plugin_packages(&self) -> Result<Vec<InstalledPluginPackage>, StorageError> {
        let mut statement = self.connection.prepare(
            "SELECT plugin_id, version, package_sha256, package_size_bytes, relative_path,
                    manifest_json, component_validated, installed_at_unix_seconds
             FROM plugin_packages
             ORDER BY plugin_id, installed_at_unix_seconds, rowid",
        )?;
        let rows = statement.query_map([], raw_plugin_package_from_row)?;
        rows.map(|row| row.map_err(StorageError::from).and_then(TryInto::try_into))
            .collect()
    }

    /// Lists installed packages and activation state in one consistent query snapshot.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails or stored integer fields are invalid.
    pub fn plugin_package_states(&self) -> Result<Vec<InstalledPluginPackageState>, StorageError> {
        let mut statement = self.connection.prepare(
            "SELECT p.plugin_id, p.version, p.package_sha256, p.package_size_bytes,
                    p.relative_path, p.manifest_json, p.component_validated,
                    p.installed_at_unix_seconds, a.version IS NOT NULL
             FROM plugin_packages p
             LEFT JOIN active_plugins a
                ON a.plugin_id = p.plugin_id AND a.version = p.version
             ORDER BY p.plugin_id, p.installed_at_unix_seconds, p.rowid",
        )?;
        let rows = statement.query_map([], |row| {
            Ok((raw_plugin_package_from_row(row)?, row.get::<_, bool>(8)?))
        })?;
        rows.map(|row| {
            let (package, active) = row?;
            Ok(InstalledPluginPackageState {
                package: package.try_into()?,
                active,
            })
        })
        .collect()
    }

    /// Returns the active version for a plugin, if enabled.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails.
    pub fn active_plugin_version(&self, plugin_id: &str) -> Result<Option<String>, StorageError> {
        self.connection
            .query_row(
                "SELECT version FROM active_plugins WHERE plugin_id = ?1",
                [plugin_id],
                |row| row.get(0),
            )
            .optional()
            .map_err(Into::into)
    }

    /// Returns the most recently installed manifest for permission comparison.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails.
    pub fn latest_plugin_manifest(&self, plugin_id: &str) -> Result<Option<String>, StorageError> {
        self.connection
            .query_row(
                "SELECT manifest_json FROM plugin_packages \
                 WHERE plugin_id = ?1 \
                 ORDER BY installed_at_unix_seconds DESC, rowid DESC LIMIT 1",
                [plugin_id],
                |row| row.get(0),
            )
            .optional()
            .map_err(Into::into)
    }

    /// Records a filesystem-committed immutable plugin package.
    ///
    /// Repeating the exact record is idempotent. Reusing a plugin ID and version for different
    /// bytes or metadata is rejected.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] for `SQLite` failures, out-of-range integers, or version conflicts.
    #[cfg(test)]
    fn insert_plugin_package(
        &mut self,
        package: &InstalledPluginPackage,
    ) -> Result<InsertPluginPackageOutcome, StorageError> {
        let package_size_bytes = sqlite_i64("package_size_bytes", package.package_size_bytes)?;
        let installed_at_unix_seconds = sqlite_i64(
            "installed_at_unix_seconds",
            package.installed_at_unix_seconds,
        )?;
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;

        if let Some(existing) =
            load_plugin_package(&transaction, &package.plugin_id, &package.version)?
        {
            if same_immutable_package(&existing, package) {
                transaction.commit()?;
                return Ok(InsertPluginPackageOutcome::AlreadyPresent);
            }
            return Err(StorageError::PluginVersionConflict {
                plugin_id: package.plugin_id.clone(),
                version: package.version.clone(),
                existing_sha256: existing.package_sha256,
                requested_sha256: package.package_sha256.clone(),
            });
        }

        transaction.execute(
            "INSERT INTO plugin_packages (
                plugin_id, version, package_sha256, package_size_bytes, relative_path,
                manifest_json, component_validated, installed_at_unix_seconds
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            (
                &package.plugin_id,
                &package.version,
                &package.package_sha256,
                package_size_bytes,
                &package.relative_path,
                &package.manifest_json,
                package.component_validated,
                installed_at_unix_seconds,
            ),
        )?;
        transaction.commit()?;
        Ok(InsertPluginPackageOutcome::Inserted)
    }

    /// Selects an installed plugin version as active.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] for database failures, invalid timestamps, or missing package
    /// references.
    pub fn activate_plugin(
        &mut self,
        plugin_id: &str,
        version: &str,
        enabled_at_unix_seconds: u64,
    ) -> Result<ActivatePluginOutcome, StorageError> {
        let enabled_at_unix_seconds =
            sqlite_i64("enabled_at_unix_seconds", enabled_at_unix_seconds)?;
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let current = transaction
            .query_row(
                "SELECT version FROM active_plugins WHERE plugin_id = ?1",
                [plugin_id],
                |row| row.get::<_, String>(0),
            )
            .optional()?;
        if current.as_deref() == Some(version) {
            transaction.commit()?;
            return Ok(ActivatePluginOutcome::AlreadyActive);
        }
        transaction.execute(
            "INSERT INTO active_plugins (plugin_id, version, enabled_at_unix_seconds)
             VALUES (?1, ?2, ?3)
             ON CONFLICT(plugin_id) DO UPDATE SET
                 version = excluded.version,
                 enabled_at_unix_seconds = excluded.enabled_at_unix_seconds",
            (plugin_id, version, enabled_at_unix_seconds),
        )?;
        transaction.commit()?;
        Ok(ActivatePluginOutcome::Activated)
    }

    /// Removes the active version for a plugin without deleting installed packages.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the state update fails.
    pub fn disable_plugin(
        &mut self,
        plugin_id: &str,
    ) -> Result<DisablePluginOutcome, StorageError> {
        let changed = self.connection.execute(
            "DELETE FROM active_plugins WHERE plugin_id = ?1",
            [plugin_id],
        )?;
        Ok(if changed == 0 {
            DisablePluginOutcome::AlreadyDisabled
        } else {
            DisablePluginOutcome::Disabled
        })
    }

    /// Reads one recoverable trashed plugin version.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails or stored integer fields are invalid.
    pub fn trashed_plugin_package(
        &self,
        plugin_id: &str,
        version: &str,
    ) -> Result<Option<TrashedPluginPackage>, StorageError> {
        load_trashed_plugin_package(&self.connection, plugin_id, version)
    }

    /// Lists all recoverable trashed plugin packages in stable order.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the query fails or stored integer fields are invalid.
    pub fn trashed_plugin_packages(&self) -> Result<Vec<TrashedPluginPackage>, StorageError> {
        let mut statement = self.connection.prepare(
            "SELECT trash_id, plugin_id, version, package_sha256, package_size_bytes,
                    original_relative_path, trash_relative_path, manifest_json,
                    component_validated, installed_at_unix_seconds, trashed_at_unix_seconds
             FROM trashed_plugin_packages
             ORDER BY trashed_at_unix_seconds, trash_id",
        )?;
        let rows = statement.query_map([], raw_trashed_plugin_package_from_row)?;
        rows.map(|row| row.map_err(StorageError::from).and_then(TryInto::try_into))
            .collect()
    }

    /// Atomically moves an installed package record into recoverable trash state.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the installed record changed, is active, trash conflicts, or
    /// the database transaction fails.
    #[cfg(test)]
    fn trash_plugin_package(&mut self, trashed: &TrashedPluginPackage) -> Result<(), StorageError> {
        let package_size_bytes =
            sqlite_i64("package_size_bytes", trashed.package.package_size_bytes)?;
        let installed_at_unix_seconds = sqlite_i64(
            "installed_at_unix_seconds",
            trashed.package.installed_at_unix_seconds,
        )?;
        let trashed_at_unix_seconds =
            sqlite_i64("trashed_at_unix_seconds", trashed.trashed_at_unix_seconds)?;
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let existing = load_plugin_package(
            &transaction,
            &trashed.package.plugin_id,
            &trashed.package.version,
        )?
        .ok_or_else(|| StorageError::PluginPackageMissing {
            plugin_id: trashed.package.plugin_id.clone(),
            version: trashed.package.version.clone(),
        })?;
        if !same_immutable_package(&existing, &trashed.package) {
            return Err(StorageError::PluginVersionConflict {
                plugin_id: trashed.package.plugin_id.clone(),
                version: trashed.package.version.clone(),
                existing_sha256: existing.package_sha256,
                requested_sha256: trashed.package.package_sha256.clone(),
            });
        }
        transaction.execute(
            "INSERT INTO trashed_plugin_packages (
                trash_id, plugin_id, version, package_sha256, package_size_bytes,
                original_relative_path, trash_relative_path, manifest_json,
                component_validated, installed_at_unix_seconds, trashed_at_unix_seconds
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
            (
                &trashed.trash_id,
                &trashed.package.plugin_id,
                &trashed.package.version,
                &trashed.package.package_sha256,
                package_size_bytes,
                &trashed.package.relative_path,
                &trashed.trash_relative_path,
                &trashed.package.manifest_json,
                trashed.package.component_validated,
                installed_at_unix_seconds,
                trashed_at_unix_seconds,
            ),
        )?;
        transaction.execute(
            "DELETE FROM plugin_packages WHERE plugin_id = ?1 AND version = ?2",
            (&trashed.package.plugin_id, &trashed.package.version),
        )?;
        transaction.commit()?;
        Ok(())
    }

    /// Atomically restores a trashed package record to installed state.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the trash entry is missing, an installed version conflicts, or
    /// the database transaction fails.
    #[cfg(test)]
    fn restore_plugin_package(
        &mut self,
        plugin_id: &str,
        version: &str,
    ) -> Result<InstalledPluginPackage, StorageError> {
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let trashed =
            load_trashed_plugin_package(&transaction, plugin_id, version)?.ok_or_else(|| {
                StorageError::TrashedPluginPackageMissing {
                    plugin_id: plugin_id.to_owned(),
                    version: version.to_owned(),
                }
            })?;
        let package_size_bytes =
            sqlite_i64("package_size_bytes", trashed.package.package_size_bytes)?;
        let installed_at_unix_seconds = sqlite_i64(
            "installed_at_unix_seconds",
            trashed.package.installed_at_unix_seconds,
        )?;
        transaction.execute(
            "INSERT INTO plugin_packages (
                plugin_id, version, package_sha256, package_size_bytes, relative_path,
                manifest_json, component_validated, installed_at_unix_seconds
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            (
                &trashed.package.plugin_id,
                &trashed.package.version,
                &trashed.package.package_sha256,
                package_size_bytes,
                &trashed.package.relative_path,
                &trashed.package.manifest_json,
                trashed.package.component_validated,
                installed_at_unix_seconds,
            ),
        )?;
        transaction.execute(
            "DELETE FROM trashed_plugin_packages WHERE trash_id = ?1",
            [&trashed.trash_id],
        )?;
        transaction.commit()?;
        Ok(trashed.package)
    }

    /// Persists a filesystem transition before the directory rename occurs.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] for malformed journal metadata, range failures, conflicts, or
    /// database errors.
    pub fn begin_plugin_file_operation(
        &mut self,
        operation: &PendingPluginFileOperation,
    ) -> Result<(), StorageError> {
        validate_plugin_file_operation(operation)?;
        let package_size_bytes =
            sqlite_i64("package_size_bytes", operation.package.package_size_bytes)?;
        let installed_at_unix_seconds = sqlite_i64(
            "installed_at_unix_seconds",
            operation.package.installed_at_unix_seconds,
        )?;
        let operation_at_unix_seconds = sqlite_i64(
            "operation_at_unix_seconds",
            operation.operation_at_unix_seconds,
        )?;
        self.connection.execute(
            "INSERT INTO plugin_file_operations (
                operation_id, kind, plugin_id, version, package_sha256, package_size_bytes,
                original_relative_path, manifest_json, component_validated,
                installed_at_unix_seconds, source_relative_directory,
                destination_relative_directory, trash_id, trash_relative_path,
                operation_at_unix_seconds
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15)",
            (
                &operation.operation_id,
                operation.kind.as_str(),
                &operation.package.plugin_id,
                &operation.package.version,
                &operation.package.package_sha256,
                package_size_bytes,
                &operation.package.relative_path,
                &operation.package.manifest_json,
                operation.package.component_validated,
                installed_at_unix_seconds,
                &operation.source_relative_directory,
                &operation.destination_relative_directory,
                &operation.trash_id,
                &operation.trash_relative_path,
                operation_at_unix_seconds,
            ),
        )?;
        Ok(())
    }

    /// Lists unfinished plugin filesystem transitions in creation order.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the journal cannot be decoded.
    pub fn pending_plugin_file_operations(
        &self,
    ) -> Result<Vec<PendingPluginFileOperation>, StorageError> {
        let mut statement = self.connection.prepare(
            "SELECT operation_id, kind, plugin_id, version, package_sha256,
                    package_size_bytes, original_relative_path, manifest_json,
                    component_validated, installed_at_unix_seconds,
                    source_relative_directory, destination_relative_directory,
                    trash_id, trash_relative_path, operation_at_unix_seconds
             FROM plugin_file_operations
             ORDER BY operation_at_unix_seconds, operation_id",
        )?;
        let rows = statement.query_map([], raw_plugin_file_operation_from_row)?;
        rows.map(|row| row.map_err(StorageError::from).and_then(TryInto::try_into))
            .collect()
    }

    /// Cancels a journal whose filesystem rename provably did not occur.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if the journal row is missing or cannot be deleted.
    pub fn cancel_plugin_file_operation(&mut self, operation_id: &str) -> Result<(), StorageError> {
        let changed = self.connection.execute(
            "DELETE FROM plugin_file_operations WHERE operation_id = ?1",
            [operation_id],
        )?;
        if changed == 1 {
            Ok(())
        } else {
            Err(StorageError::PluginFileOperationMissing(
                operation_id.to_owned(),
            ))
        }
    }

    /// Completes the `SQLite` side of a directory rename and clears its journal atomically.
    ///
    /// # Errors
    ///
    /// Returns [`StorageError`] if journal metadata changed, lifecycle state conflicts, or the
    /// transaction fails.
    pub fn complete_plugin_file_operation(
        &mut self,
        operation: &PendingPluginFileOperation,
    ) -> Result<(), StorageError> {
        validate_plugin_file_operation(operation)?;
        let transaction = self
            .connection
            .transaction_with_behavior(TransactionBehavior::Immediate)?;
        let stored = load_plugin_file_operation(&transaction, &operation.operation_id)?
            .ok_or_else(|| {
                StorageError::PluginFileOperationMissing(operation.operation_id.clone())
            })?;
        if stored != *operation {
            return Err(StorageError::PluginFileOperationMismatch(
                operation.operation_id.clone(),
            ));
        }
        match operation.kind {
            PluginFileOperationKind::Install => {
                complete_install_operation(&transaction, operation)?;
            }
            PluginFileOperationKind::Trash => {
                complete_trash_operation(&transaction, operation)?;
            }
            PluginFileOperationKind::Restore => {
                complete_restore_operation(&transaction, operation)?;
            }
        }
        transaction.execute(
            "DELETE FROM plugin_file_operations WHERE operation_id = ?1",
            [&operation.operation_id],
        )?;
        transaction.commit()?;
        Ok(())
    }
}

fn validate_plugin_file_operation(
    operation: &PendingPluginFileOperation,
) -> Result<(), StorageError> {
    let has_trash = operation.trash_id.is_some() && operation.trash_relative_path.is_some();
    let valid = !operation.operation_id.is_empty()
        && operation.source_relative_directory != operation.destination_relative_directory
        && match operation.kind {
            PluginFileOperationKind::Install => {
                operation.trash_id.is_none() && operation.trash_relative_path.is_none()
            }
            PluginFileOperationKind::Trash | PluginFileOperationKind::Restore => has_trash,
        };
    if valid {
        Ok(())
    } else {
        Err(StorageError::InvalidPluginFileOperation(
            operation.operation_id.clone(),
        ))
    }
}

fn complete_install_operation(
    transaction: &Transaction<'_>,
    operation: &PendingPluginFileOperation,
) -> Result<(), StorageError> {
    if let Some(existing) = load_plugin_package(
        transaction,
        &operation.package.plugin_id,
        &operation.package.version,
    )? {
        if same_immutable_package(&existing, &operation.package) {
            return Ok(());
        }
        return Err(StorageError::PluginVersionConflict {
            plugin_id: operation.package.plugin_id.clone(),
            version: operation.package.version.clone(),
            existing_sha256: existing.package_sha256,
            requested_sha256: operation.package.package_sha256.clone(),
        });
    }
    insert_plugin_package_row(transaction, &operation.package)
}

fn complete_trash_operation(
    transaction: &Transaction<'_>,
    operation: &PendingPluginFileOperation,
) -> Result<(), StorageError> {
    let existing = load_plugin_package(
        transaction,
        &operation.package.plugin_id,
        &operation.package.version,
    )?
    .ok_or_else(|| StorageError::PluginPackageMissing {
        plugin_id: operation.package.plugin_id.clone(),
        version: operation.package.version.clone(),
    })?;
    if !same_immutable_package(&existing, &operation.package) {
        return Err(StorageError::PluginVersionConflict {
            plugin_id: operation.package.plugin_id.clone(),
            version: operation.package.version.clone(),
            existing_sha256: existing.package_sha256,
            requested_sha256: operation.package.package_sha256.clone(),
        });
    }
    let trashed = operation_trashed_package(operation)?;
    insert_trashed_plugin_package_row(transaction, &trashed)?;
    transaction.execute(
        "DELETE FROM plugin_packages WHERE plugin_id = ?1 AND version = ?2",
        (&operation.package.plugin_id, &operation.package.version),
    )?;
    Ok(())
}

fn complete_restore_operation(
    transaction: &Transaction<'_>,
    operation: &PendingPluginFileOperation,
) -> Result<(), StorageError> {
    let expected = operation_trashed_package(operation)?;
    let existing = load_trashed_plugin_package(
        transaction,
        &operation.package.plugin_id,
        &operation.package.version,
    )?
    .ok_or_else(|| StorageError::TrashedPluginPackageMissing {
        plugin_id: operation.package.plugin_id.clone(),
        version: operation.package.version.clone(),
    })?;
    if existing != expected {
        return Err(StorageError::PluginFileOperationMismatch(
            operation.operation_id.clone(),
        ));
    }
    insert_plugin_package_row(transaction, &operation.package)?;
    transaction.execute(
        "DELETE FROM trashed_plugin_packages WHERE trash_id = ?1",
        [&existing.trash_id],
    )?;
    Ok(())
}

fn operation_trashed_package(
    operation: &PendingPluginFileOperation,
) -> Result<TrashedPluginPackage, StorageError> {
    Ok(TrashedPluginPackage {
        trash_id: operation.trash_id.clone().ok_or_else(|| {
            StorageError::InvalidPluginFileOperation(operation.operation_id.clone())
        })?,
        package: operation.package.clone(),
        trash_relative_path: operation.trash_relative_path.clone().ok_or_else(|| {
            StorageError::InvalidPluginFileOperation(operation.operation_id.clone())
        })?,
        trashed_at_unix_seconds: operation.operation_at_unix_seconds,
    })
}

fn insert_plugin_package_row(
    transaction: &Transaction<'_>,
    package: &InstalledPluginPackage,
) -> Result<(), StorageError> {
    let package_size_bytes = sqlite_i64("package_size_bytes", package.package_size_bytes)?;
    let installed_at_unix_seconds = sqlite_i64(
        "installed_at_unix_seconds",
        package.installed_at_unix_seconds,
    )?;
    transaction.execute(
        "INSERT INTO plugin_packages (
            plugin_id, version, package_sha256, package_size_bytes, relative_path,
            manifest_json, component_validated, installed_at_unix_seconds
         ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
        (
            &package.plugin_id,
            &package.version,
            &package.package_sha256,
            package_size_bytes,
            &package.relative_path,
            &package.manifest_json,
            package.component_validated,
            installed_at_unix_seconds,
        ),
    )?;
    Ok(())
}

fn insert_trashed_plugin_package_row(
    transaction: &Transaction<'_>,
    trashed: &TrashedPluginPackage,
) -> Result<(), StorageError> {
    let package_size_bytes = sqlite_i64("package_size_bytes", trashed.package.package_size_bytes)?;
    let installed_at_unix_seconds = sqlite_i64(
        "installed_at_unix_seconds",
        trashed.package.installed_at_unix_seconds,
    )?;
    let trashed_at_unix_seconds =
        sqlite_i64("trashed_at_unix_seconds", trashed.trashed_at_unix_seconds)?;
    transaction.execute(
        "INSERT INTO trashed_plugin_packages (
            trash_id, plugin_id, version, package_sha256, package_size_bytes,
            original_relative_path, trash_relative_path, manifest_json,
            component_validated, installed_at_unix_seconds, trashed_at_unix_seconds
         ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
        (
            &trashed.trash_id,
            &trashed.package.plugin_id,
            &trashed.package.version,
            &trashed.package.package_sha256,
            package_size_bytes,
            &trashed.package.relative_path,
            &trashed.trash_relative_path,
            &trashed.package.manifest_json,
            trashed.package.component_validated,
            installed_at_unix_seconds,
            trashed_at_unix_seconds,
        ),
    )?;
    Ok(())
}

fn same_immutable_package(
    existing: &InstalledPluginPackage,
    requested: &InstalledPluginPackage,
) -> bool {
    existing.plugin_id == requested.plugin_id
        && existing.version == requested.version
        && existing.package_sha256 == requested.package_sha256
        && existing.package_size_bytes == requested.package_size_bytes
        && existing.relative_path == requested.relative_path
        && existing.manifest_json == requested.manifest_json
        && existing.component_validated == requested.component_validated
}

fn load_plugin_package(
    connection: &Connection,
    plugin_id: &str,
    version: &str,
) -> Result<Option<InstalledPluginPackage>, StorageError> {
    let raw = connection
        .query_row(
            "SELECT plugin_id, version, package_sha256, package_size_bytes, relative_path,
                    manifest_json, component_validated, installed_at_unix_seconds
             FROM plugin_packages WHERE plugin_id = ?1 AND version = ?2",
            (plugin_id, version),
            raw_plugin_package_from_row,
        )
        .optional()?;
    raw.map(TryInto::try_into).transpose()
}

fn raw_plugin_package_from_row(
    row: &rusqlite::Row<'_>,
) -> Result<RawInstalledPluginPackage, rusqlite::Error> {
    Ok(RawInstalledPluginPackage {
        plugin_id: row.get(0)?,
        version: row.get(1)?,
        package_sha256: row.get(2)?,
        package_size_bytes: row.get(3)?,
        relative_path: row.get(4)?,
        manifest_json: row.get(5)?,
        component_validated: row.get(6)?,
        installed_at_unix_seconds: row.get(7)?,
    })
}

struct RawInstalledPluginPackage {
    plugin_id: String,
    version: String,
    package_sha256: String,
    package_size_bytes: i64,
    relative_path: String,
    manifest_json: String,
    component_validated: bool,
    installed_at_unix_seconds: i64,
}

impl TryFrom<RawInstalledPluginPackage> for InstalledPluginPackage {
    type Error = StorageError;

    fn try_from(raw: RawInstalledPluginPackage) -> Result<Self, Self::Error> {
        Ok(Self {
            plugin_id: raw.plugin_id,
            version: raw.version,
            package_sha256: raw.package_sha256,
            package_size_bytes: stored_u64("package_size_bytes", raw.package_size_bytes)?,
            relative_path: raw.relative_path,
            manifest_json: raw.manifest_json,
            component_validated: raw.component_validated,
            installed_at_unix_seconds: stored_u64(
                "installed_at_unix_seconds",
                raw.installed_at_unix_seconds,
            )?,
        })
    }
}

fn load_trashed_plugin_package(
    connection: &Connection,
    plugin_id: &str,
    version: &str,
) -> Result<Option<TrashedPluginPackage>, StorageError> {
    let raw = connection
        .query_row(
            "SELECT trash_id, plugin_id, version, package_sha256, package_size_bytes,
                    original_relative_path, trash_relative_path, manifest_json,
                    component_validated, installed_at_unix_seconds, trashed_at_unix_seconds
             FROM trashed_plugin_packages WHERE plugin_id = ?1 AND version = ?2",
            (plugin_id, version),
            raw_trashed_plugin_package_from_row,
        )
        .optional()?;
    raw.map(TryInto::try_into).transpose()
}

fn raw_trashed_plugin_package_from_row(
    row: &rusqlite::Row<'_>,
) -> Result<RawTrashedPluginPackage, rusqlite::Error> {
    Ok(RawTrashedPluginPackage {
        trash_id: row.get(0)?,
        plugin_id: row.get(1)?,
        version: row.get(2)?,
        package_sha256: row.get(3)?,
        package_size_bytes: row.get(4)?,
        original_relative_path: row.get(5)?,
        trash_relative_path: row.get(6)?,
        manifest_json: row.get(7)?,
        component_validated: row.get(8)?,
        installed_at_unix_seconds: row.get(9)?,
        trashed_at_unix_seconds: row.get(10)?,
    })
}

struct RawTrashedPluginPackage {
    trash_id: String,
    plugin_id: String,
    version: String,
    package_sha256: String,
    package_size_bytes: i64,
    original_relative_path: String,
    trash_relative_path: String,
    manifest_json: String,
    component_validated: bool,
    installed_at_unix_seconds: i64,
    trashed_at_unix_seconds: i64,
}

impl TryFrom<RawTrashedPluginPackage> for TrashedPluginPackage {
    type Error = StorageError;

    fn try_from(raw: RawTrashedPluginPackage) -> Result<Self, Self::Error> {
        Ok(Self {
            trash_id: raw.trash_id,
            package: InstalledPluginPackage {
                plugin_id: raw.plugin_id,
                version: raw.version,
                package_sha256: raw.package_sha256,
                package_size_bytes: stored_u64("package_size_bytes", raw.package_size_bytes)?,
                relative_path: raw.original_relative_path,
                manifest_json: raw.manifest_json,
                component_validated: raw.component_validated,
                installed_at_unix_seconds: stored_u64(
                    "installed_at_unix_seconds",
                    raw.installed_at_unix_seconds,
                )?,
            },
            trash_relative_path: raw.trash_relative_path,
            trashed_at_unix_seconds: stored_u64(
                "trashed_at_unix_seconds",
                raw.trashed_at_unix_seconds,
            )?,
        })
    }
}

fn load_plugin_file_operation(
    connection: &Connection,
    operation_id: &str,
) -> Result<Option<PendingPluginFileOperation>, StorageError> {
    let raw = connection
        .query_row(
            "SELECT operation_id, kind, plugin_id, version, package_sha256,
                    package_size_bytes, original_relative_path, manifest_json,
                    component_validated, installed_at_unix_seconds,
                    source_relative_directory, destination_relative_directory,
                    trash_id, trash_relative_path, operation_at_unix_seconds
             FROM plugin_file_operations WHERE operation_id = ?1",
            [operation_id],
            raw_plugin_file_operation_from_row,
        )
        .optional()?;
    raw.map(TryInto::try_into).transpose()
}

fn raw_plugin_file_operation_from_row(
    row: &rusqlite::Row<'_>,
) -> Result<RawPluginFileOperation, rusqlite::Error> {
    Ok(RawPluginFileOperation {
        operation_id: row.get(0)?,
        kind: row.get(1)?,
        plugin_id: row.get(2)?,
        version: row.get(3)?,
        package_sha256: row.get(4)?,
        package_size_bytes: row.get(5)?,
        original_relative_path: row.get(6)?,
        manifest_json: row.get(7)?,
        component_validated: row.get(8)?,
        installed_at_unix_seconds: row.get(9)?,
        source_relative_directory: row.get(10)?,
        destination_relative_directory: row.get(11)?,
        trash_id: row.get(12)?,
        trash_relative_path: row.get(13)?,
        operation_at_unix_seconds: row.get(14)?,
    })
}

struct RawPluginFileOperation {
    operation_id: String,
    kind: String,
    plugin_id: String,
    version: String,
    package_sha256: String,
    package_size_bytes: i64,
    original_relative_path: String,
    manifest_json: String,
    component_validated: bool,
    installed_at_unix_seconds: i64,
    source_relative_directory: String,
    destination_relative_directory: String,
    trash_id: Option<String>,
    trash_relative_path: Option<String>,
    operation_at_unix_seconds: i64,
}

impl TryFrom<RawPluginFileOperation> for PendingPluginFileOperation {
    type Error = StorageError;

    fn try_from(raw: RawPluginFileOperation) -> Result<Self, Self::Error> {
        let operation = Self {
            operation_id: raw.operation_id,
            kind: PluginFileOperationKind::try_from(raw.kind.as_str())?,
            package: InstalledPluginPackage {
                plugin_id: raw.plugin_id,
                version: raw.version,
                package_sha256: raw.package_sha256,
                package_size_bytes: stored_u64("package_size_bytes", raw.package_size_bytes)?,
                relative_path: raw.original_relative_path,
                manifest_json: raw.manifest_json,
                component_validated: raw.component_validated,
                installed_at_unix_seconds: stored_u64(
                    "installed_at_unix_seconds",
                    raw.installed_at_unix_seconds,
                )?,
            },
            source_relative_directory: raw.source_relative_directory,
            destination_relative_directory: raw.destination_relative_directory,
            trash_id: raw.trash_id,
            trash_relative_path: raw.trash_relative_path,
            operation_at_unix_seconds: stored_u64(
                "operation_at_unix_seconds",
                raw.operation_at_unix_seconds,
            )?,
        };
        validate_plugin_file_operation(&operation)?;
        Ok(operation)
    }
}

fn sqlite_i64(field: &'static str, value: u64) -> Result<i64, StorageError> {
    i64::try_from(value).map_err(|_| StorageError::IntegerOutOfRange { field, value })
}

fn stored_u64(field: &'static str, value: i64) -> Result<u64, StorageError> {
    u64::try_from(value).map_err(|_| StorageError::InvalidStoredInteger { field, value })
}

#[derive(Debug, Clone, Copy)]
struct Migration {
    version: u32,
    name: &'static str,
    sql: &'static str,
}

fn validate_migrations(migrations: &[Migration]) -> Result<(), StorageError> {
    if migrations.is_empty()
        || migrations.last().map(|item| item.version) != Some(CURRENT_SCHEMA_VERSION)
    {
        return Err(StorageError::InvalidMigrationSequence);
    }
    for (index, migration) in migrations.iter().enumerate() {
        let expected =
            u32::try_from(index + 1).map_err(|_| StorageError::InvalidMigrationSequence)?;
        if migration.version != expected || migration.name.is_empty() {
            return Err(StorageError::InvalidMigrationSequence);
        }
    }
    Ok(())
}

fn validate_database_path(path: &Path) -> Result<(), StorageError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_file() => Ok(()),
        Ok(_) => Err(StorageError::InvalidDatabaseFile(path.to_owned())),
        Err(source) if source.kind() == io::ErrorKind::NotFound => {
            let parent = path
                .parent()
                .ok_or_else(|| StorageError::DatabaseParentMissing(path.to_owned()))?;
            let metadata = fs::symlink_metadata(parent).map_err(|source| StorageError::Io {
                operation: "inspect the state database parent",
                path: parent.to_owned(),
                source,
            })?;
            if metadata.file_type().is_dir() {
                Ok(())
            } else {
                Err(StorageError::DatabaseParentMissing(parent.to_owned()))
            }
        }
        Err(source) => Err(StorageError::Io {
            operation: "inspect the state database path",
            path: path.to_owned(),
            source,
        }),
    }
}

fn migrate_and_validate(
    transaction: &Transaction<'_>,
    expected_identity: StateDatabaseIdentity,
    migrations: &[Migration],
) -> Result<(), StorageError> {
    let application_id = pragma_i64(transaction, "application_id")?;
    let found_version = pragma_i64(transaction, "user_version")?;
    let found_version = u32::try_from(found_version)
        .map_err(|_| StorageError::InvalidSchemaVersion(found_version))?;

    let new_database = application_id == 0 && found_version == 0;
    if new_database && user_object_count(transaction)? != 0 {
        return Err(StorageError::UnrecognizedDatabase);
    }
    if !new_database && application_id != APPLICATION_ID {
        return Err(StorageError::ForeignDatabase { application_id });
    }
    if found_version > CURRENT_SCHEMA_VERSION {
        return Err(StorageError::UnsupportedSchemaVersion {
            found: found_version,
            supported: CURRENT_SCHEMA_VERSION,
        });
    }

    if new_database {
        transaction.pragma_update(None, "application_id", APPLICATION_ID)?;
    }

    for migration in migrations
        .iter()
        .filter(|migration| migration.version > found_version)
    {
        transaction.execute_batch(migration.sql)?;
        transaction.execute(
            "INSERT INTO schema_migrations (version, name, applied_at_unix_seconds) \
             VALUES (?1, ?2, unixepoch())",
            (migration.version, migration.name),
        )?;
        transaction.pragma_update(None, "user_version", migration.version)?;
    }

    validate_migration_history(transaction, migrations)?;
    bind_or_validate_identity(transaction, expected_identity, found_version == 0)
}

fn pragma_i64(transaction: &Transaction<'_>, name: &str) -> rusqlite::Result<i64> {
    transaction.pragma_query_value(None, name, |row| row.get(0))
}

fn user_object_count(transaction: &Transaction<'_>) -> rusqlite::Result<u32> {
    transaction.query_row(
        "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'",
        [],
        |row| row.get(0),
    )
}

fn validate_migration_history(
    transaction: &Transaction<'_>,
    migrations: &[Migration],
) -> Result<(), StorageError> {
    for migration in migrations {
        let recorded_name = transaction
            .query_row(
                "SELECT name FROM schema_migrations WHERE version = ?1",
                [migration.version],
                |row| row.get::<_, String>(0),
            )
            .optional()?;
        if recorded_name.as_deref() != Some(migration.name) {
            return Err(StorageError::MigrationHistoryMismatch {
                version: migration.version,
                expected_name: migration.name,
                found_name: recorded_name,
            });
        }
    }
    Ok(())
}

fn bind_or_validate_identity(
    transaction: &Transaction<'_>,
    expected: StateDatabaseIdentity,
    insert: bool,
) -> Result<(), StorageError> {
    let expected_workspace_id = expected.workspace_id.to_string();
    let expected_host_triple = expected.host_triple.as_str();
    let expected_created_at =
        i64::try_from(expected.workspace_created_at_unix_seconds).map_err(|_| {
            StorageError::TimestampOutOfRange(expected.workspace_created_at_unix_seconds)
        })?;

    if insert {
        transaction.execute(
            "INSERT INTO host_identity \
             (singleton, workspace_id, host_triple, workspace_created_at_unix_seconds) \
             VALUES (1, ?1, ?2, ?3)",
            (
                &expected_workspace_id,
                expected_host_triple,
                expected_created_at,
            ),
        )?;
        return Ok(());
    }

    let actual = transaction
        .query_row(
            "SELECT workspace_id, host_triple, workspace_created_at_unix_seconds \
             FROM host_identity WHERE singleton = 1",
            [],
            |row| {
                Ok((
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, i64>(2)?,
                ))
            },
        )
        .optional()?;
    let expected_tuple = (
        expected_workspace_id,
        expected_host_triple.to_owned(),
        expected_created_at,
    );
    if actual.as_ref() != Some(&expected_tuple) {
        return Err(StorageError::IdentityMismatch {
            expected_workspace_id: expected_tuple.0,
            expected_host_triple: expected_tuple.1,
            actual,
        });
    }
    Ok(())
}

/// State database initialization, compatibility, or transaction error.
#[derive(Debug, Error)]
pub enum StorageError {
    /// File-system inspection failed before `SQLite` was opened.
    #[error("failed to {operation} at '{}': {source}", path.display())]
    Io {
        /// Human-readable operation stage.
        operation: &'static str,
        /// Path involved in the failure.
        path: PathBuf,
        /// Operating system error.
        #[source]
        source: io::Error,
    },
    /// `SQLite` returned an error.
    #[error(transparent)]
    Sqlite(#[from] rusqlite::Error),
    /// The database path exists but is not a regular file.
    #[error("state database path is not a regular file: '{}'", .0.display())]
    InvalidDatabaseFile(PathBuf),
    /// The database parent is missing or not a real directory.
    #[error("state database parent is unavailable: '{}'", .0.display())]
    DatabaseParentMissing(PathBuf),
    /// Migration definitions are not contiguous through the current version.
    #[error("state database migration sequence is invalid")]
    InvalidMigrationSequence,
    /// A signed `SQLite` schema version could not be represented safely.
    #[error("state database schema version is invalid: {0}")]
    InvalidSchemaVersion(i64),
    /// An empty application ID was paired with unknown user-created objects.
    #[error("state database contains objects not owned by SoftPilot")]
    UnrecognizedDatabase,
    /// The `SQLite` application ID belongs to another application.
    #[error("state database application ID is not SoftPilot: {application_id}")]
    ForeignDatabase {
        /// Application ID read from the database header.
        application_id: i64,
    },
    /// The database was written by a newer schema.
    #[error("state database schema version {found} is newer than supported version {supported}")]
    UnsupportedSchemaVersion {
        /// Version stored in `PRAGMA user_version`.
        found: u32,
        /// Latest version supported by this build.
        supported: u32,
    },
    /// Applied migration metadata does not match the compiled migration list.
    #[error(
        "state database migration {version} history mismatch: expected '{expected_name}', \
         found {found_name:?}"
    )]
    MigrationHistoryMismatch {
        /// Migration version being validated.
        version: u32,
        /// Compiled stable migration name.
        expected_name: &'static str,
        /// Name read from the database, if a row existed.
        found_name: Option<String>,
    },
    /// The database is bound to a different workspace or host directory.
    #[error(
        "state database identity mismatch: expected workspace {expected_workspace_id} \
         host {expected_host_triple}, found {actual:?}"
    )]
    IdentityMismatch {
        /// Expected workspace ID.
        expected_workspace_id: String,
        /// Expected host triple.
        expected_host_triple: String,
        /// Stored workspace ID, host triple, and creation time.
        actual: Option<(String, String, i64)>,
    },
    /// Workspace timestamps must fit `SQLite`'s signed integer representation.
    #[error("workspace creation timestamp is out of SQLite range: {0}")]
    TimestampOutOfRange(u64),
    /// `SQLite` refused the required WAL journal mode.
    #[error("state database did not enter WAL journal mode: {0}")]
    JournalMode(String),
    /// A Rust unsigned value cannot be represented by a database INTEGER.
    #[error("{field} is out of SQLite INTEGER range: {value}")]
    IntegerOutOfRange {
        /// Field being converted.
        field: &'static str,
        /// Unsigned value supplied by the caller.
        value: u64,
    },
    /// A persisted database INTEGER violated the non-negative storage contract.
    #[error("stored {field} is outside its unsigned range: {value}")]
    InvalidStoredInteger {
        /// Field being decoded.
        field: &'static str,
        /// Signed value read from `SQLite`.
        value: i64,
    },
    /// An immutable plugin ID/version was already associated with different state.
    #[error(
        "plugin {plugin_id} version {version} already has SHA-256 {existing_sha256}; requested {requested_sha256}"
    )]
    PluginVersionConflict {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact plugin version.
        version: String,
        /// Existing immutable package digest.
        existing_sha256: String,
        /// Requested package digest.
        requested_sha256: String,
    },
    /// An installed plugin package required by a state transition is missing.
    #[error("installed plugin package is missing: {plugin_id} version {version}")]
    PluginPackageMissing {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact plugin version.
        version: String,
    },
    /// A recoverable trash entry required by restoration is missing.
    #[error("trashed plugin package is missing: {plugin_id} version {version}")]
    TrashedPluginPackageMissing {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact plugin version.
        version: String,
    },
    /// A persisted plugin filesystem operation has an unknown kind.
    #[error("plugin file operation has unknown kind: {0}")]
    InvalidPluginFileOperationKind(String),
    /// A plugin filesystem operation violates the journal contract.
    #[error("plugin file operation is invalid: {0}")]
    InvalidPluginFileOperation(String),
    /// A requested plugin filesystem journal row does not exist.
    #[error("plugin file operation is missing: {0}")]
    PluginFileOperationMissing(String),
    /// Supplied operation metadata differs from its durable journal row.
    #[error("plugin file operation journal changed: {0}")]
    PluginFileOperationMismatch(String),
}

/// Error returned by a state transaction while preserving domain-specific operation failures.
#[derive(Debug, Error)]
pub enum TransactionError<E> {
    /// `SQLite` could not begin or commit the transaction.
    #[error(transparent)]
    Storage(StorageError),
    /// The caller rejected its operation; the database transaction was rolled back.
    #[error("state transaction operation failed: {0}")]
    Operation(E),
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{env, str::FromStr, thread};

    struct TestDirectory(PathBuf);

    #[derive(Debug, Error, PartialEq, Eq)]
    #[error("injected operation failure")]
    struct InjectedFailure;

    impl TestDirectory {
        fn new(label: &str) -> Self {
            let path = env::temp_dir().join(format!(
                "softpilot-storage-test-{label}-{}",
                WorkspaceId::generate()
            ));
            fs::create_dir(&path).expect("create storage test directory");
            Self(path)
        }

        fn database_path(&self) -> PathBuf {
            self.0.join("state.db")
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn identity() -> StateDatabaseIdentity {
        StateDatabaseIdentity {
            workspace_id: WorkspaceId::from_str("018f47d8-caa7-7c28-b6e2-11a8c40d8a42")
                .expect("fixed workspace ID"),
            host_triple: HostTriple::WindowsX86_64,
            workspace_created_at_unix_seconds: 1_700_000_000,
        }
    }

    #[test]
    fn creates_reopens_and_binds_the_current_schema() {
        let directory = TestDirectory::new("create");
        let path = directory.database_path();

        let database = StateDatabase::open(&path, identity()).expect("initialize database");
        assert_eq!(database.schema_version(), CURRENT_SCHEMA_VERSION);
        assert_eq!(database.path(), path);
        drop(database);

        StateDatabase::open(&path, identity()).expect("reopen current database");
        let connection = Connection::open(path).expect("inspect database");
        assert_eq!(
            pragma_i64_for_connection(&connection, "application_id"),
            APPLICATION_ID
        );
        assert_eq!(
            pragma_i64_for_connection(&connection, "user_version"),
            i64::from(CURRENT_SCHEMA_VERSION)
        );
    }

    #[test]
    fn migrates_a_recognized_empty_version_zero_database() {
        let directory = TestDirectory::new("migrate-zero");
        let path = directory.database_path();
        let connection = Connection::open(&path).expect("create version zero database");
        connection
            .pragma_update(None, "application_id", APPLICATION_ID)
            .expect("mark database as SoftPilot");
        drop(connection);

        StateDatabase::open(&path, identity()).expect("migrate recognized database");
        let connection = Connection::open(path).expect("inspect migrated database");
        assert_eq!(
            pragma_i64_for_connection(&connection, "user_version"),
            i64::from(CURRENT_SCHEMA_VERSION)
        );
    }

    #[test]
    fn migrates_version_one_to_plugin_package_schema() {
        let directory = TestDirectory::new("migrate-one");
        let path = directory.database_path();
        let connection = Connection::open(&path).expect("create version one database");
        connection
            .execute_batch(MIGRATIONS[0].sql)
            .expect("apply version one schema");
        connection
            .execute(
                "INSERT INTO schema_migrations \
                 (version, name, applied_at_unix_seconds) VALUES (1, ?1, 0)",
                [MIGRATIONS[0].name],
            )
            .expect("record version one migration");
        connection
            .execute(
                "INSERT INTO host_identity \
                 (singleton, workspace_id, host_triple, workspace_created_at_unix_seconds) \
                 VALUES (1, ?1, ?2, ?3)",
                (
                    identity().workspace_id.to_string(),
                    identity().host_triple.as_str(),
                    i64::try_from(identity().workspace_created_at_unix_seconds)
                        .expect("fixture timestamp"),
                ),
            )
            .expect("bind version one identity");
        connection
            .pragma_update(None, "application_id", APPLICATION_ID)
            .expect("set application ID");
        connection
            .pragma_update(None, "user_version", 1)
            .expect("set version one");
        drop(connection);

        StateDatabase::open(&path, identity()).expect("migrate version one database");
        let connection = Connection::open(path).expect("inspect migrated database");
        let table_count: u32 = connection
            .query_row(
                "SELECT count(*) FROM sqlite_schema \
                 WHERE type = 'table' AND name = 'plugin_packages'",
                [],
                |row| row.get(0),
            )
            .expect("inspect plugin package table");
        assert_eq!(table_count, 1);
        assert_eq!(
            pragma_i64_for_connection(&connection, "user_version"),
            i64::from(CURRENT_SCHEMA_VERSION)
        );
    }

    #[test]
    fn migrates_version_two_to_active_plugin_schema() {
        let directory = TestDirectory::new("migrate-two");
        let path = directory.database_path();
        let connection = Connection::open(&path).expect("create version two database");
        for migration in &MIGRATIONS[..2] {
            connection
                .execute_batch(migration.sql)
                .expect("apply old migration");
            connection
                .execute(
                    "INSERT INTO schema_migrations \
                     (version, name, applied_at_unix_seconds) VALUES (?1, ?2, 0)",
                    (migration.version, migration.name),
                )
                .expect("record old migration");
        }
        connection
            .execute(
                "INSERT INTO host_identity \
                 (singleton, workspace_id, host_triple, workspace_created_at_unix_seconds) \
                 VALUES (1, ?1, ?2, ?3)",
                (
                    identity().workspace_id.to_string(),
                    identity().host_triple.as_str(),
                    i64::try_from(identity().workspace_created_at_unix_seconds)
                        .expect("fixture timestamp"),
                ),
            )
            .expect("bind version two identity");
        connection
            .pragma_update(None, "application_id", APPLICATION_ID)
            .expect("set application ID");
        connection
            .pragma_update(None, "user_version", 2)
            .expect("set version two");
        drop(connection);

        StateDatabase::open(&path, identity()).expect("migrate version two database");
        let connection = Connection::open(path).expect("inspect migrated database");
        let table_count: u32 = connection
            .query_row(
                "SELECT count(*) FROM sqlite_schema \
                 WHERE type = 'table' AND name = 'active_plugins'",
                [],
                |row| row.get(0),
            )
            .expect("inspect active plugin table");
        assert_eq!(table_count, 1);
        assert_eq!(
            pragma_i64_for_connection(&connection, "user_version"),
            i64::from(CURRENT_SCHEMA_VERSION)
        );
    }

    #[test]
    fn migrates_version_three_to_plugin_trash_schema() {
        let directory = TestDirectory::new("migrate-three");
        let path = directory.database_path();
        let connection = Connection::open(&path).expect("create version three database");
        connection
            .pragma_update(None, "foreign_keys", "ON")
            .expect("enable foreign keys");
        for migration in &MIGRATIONS[..3] {
            connection
                .execute_batch(migration.sql)
                .expect("apply old migration");
            connection
                .execute(
                    "INSERT INTO schema_migrations \
                     (version, name, applied_at_unix_seconds) VALUES (?1, ?2, 0)",
                    (migration.version, migration.name),
                )
                .expect("record old migration");
        }
        connection
            .execute(
                "INSERT INTO host_identity \
                 (singleton, workspace_id, host_triple, workspace_created_at_unix_seconds) \
                 VALUES (1, ?1, ?2, ?3)",
                (
                    identity().workspace_id.to_string(),
                    identity().host_triple.as_str(),
                    i64::try_from(identity().workspace_created_at_unix_seconds)
                        .expect("fixture timestamp"),
                ),
            )
            .expect("bind version three identity");
        connection
            .pragma_update(None, "application_id", APPLICATION_ID)
            .expect("set application ID");
        connection
            .pragma_update(None, "user_version", 3)
            .expect("set version three");
        drop(connection);

        StateDatabase::open(&path, identity()).expect("migrate version three database");
        let connection = Connection::open(path).expect("inspect migrated database");
        let table_count: u32 = connection
            .query_row(
                "SELECT count(*) FROM sqlite_schema \
                 WHERE type = 'table' AND name = 'trashed_plugin_packages'",
                [],
                |row| row.get(0),
            )
            .expect("inspect plugin trash table");
        assert_eq!(table_count, 1);
        assert_eq!(
            pragma_i64_for_connection(&connection, "user_version"),
            i64::from(CURRENT_SCHEMA_VERSION)
        );
    }

    #[test]
    fn migrates_version_four_to_plugin_operation_journal() {
        let directory = TestDirectory::new("migrate-four");
        let path = directory.database_path();
        let connection = Connection::open(&path).expect("create version four database");
        connection
            .pragma_update(None, "foreign_keys", "ON")
            .expect("enable foreign keys");
        for migration in &MIGRATIONS[..4] {
            connection
                .execute_batch(migration.sql)
                .expect("apply old migration");
            connection
                .execute(
                    "INSERT INTO schema_migrations \
                     (version, name, applied_at_unix_seconds) VALUES (?1, ?2, 0)",
                    (migration.version, migration.name),
                )
                .expect("record old migration");
        }
        connection
            .execute(
                "INSERT INTO host_identity \
                 (singleton, workspace_id, host_triple, workspace_created_at_unix_seconds) \
                 VALUES (1, ?1, ?2, ?3)",
                (
                    identity().workspace_id.to_string(),
                    identity().host_triple.as_str(),
                    i64::try_from(identity().workspace_created_at_unix_seconds)
                        .expect("fixture timestamp"),
                ),
            )
            .expect("bind version four identity");
        connection
            .pragma_update(None, "application_id", APPLICATION_ID)
            .expect("set application ID");
        connection
            .pragma_update(None, "user_version", 4)
            .expect("set version four");
        drop(connection);

        StateDatabase::open(&path, identity()).expect("migrate version four database");
        let connection = Connection::open(path).expect("inspect migrated database");
        let table_count: u32 = connection
            .query_row(
                "SELECT count(*) FROM sqlite_schema \
                 WHERE type = 'table' AND name = 'plugin_file_operations'",
                [],
                |row| row.get(0),
            )
            .expect("inspect operation journal table");
        assert_eq!(table_count, 1);
        assert_eq!(
            pragma_i64_for_connection(&connection, "user_version"),
            i64::from(CURRENT_SCHEMA_VERSION)
        );
    }

    #[test]
    fn concurrent_openers_converge_on_one_schema() {
        let directory = TestDirectory::new("concurrent-open");
        let path = directory.database_path();
        let first_path = path.clone();
        let second_path = path.clone();

        let first = thread::spawn(move || StateDatabase::open(first_path, identity()));
        let second = thread::spawn(move || StateDatabase::open(second_path, identity()));
        first
            .join()
            .expect("first opener thread")
            .expect("first opener");
        second
            .join()
            .expect("second opener thread")
            .expect("second opener");

        StateDatabase::open(path, identity()).expect("reopen converged database");
    }

    #[test]
    fn rejects_foreign_unknown_and_newer_databases_without_claiming_them() {
        let directory = TestDirectory::new("foreign");
        let foreign_path = directory.0.join("foreign.db");
        let connection = Connection::open(&foreign_path).expect("create foreign database");
        connection
            .execute("CREATE TABLE foreign_data (value TEXT)", [])
            .expect("create foreign table");
        drop(connection);

        assert!(matches!(
            StateDatabase::open(&foreign_path, identity()),
            Err(StorageError::UnrecognizedDatabase)
        ));
        let connection = Connection::open(&foreign_path).expect("reopen foreign database");
        assert_eq!(pragma_i64_for_connection(&connection, "application_id"), 0);
        assert_eq!(pragma_i64_for_connection(&connection, "user_version"), 0);
        drop(connection);

        let newer_path = directory.0.join("newer.db");
        let connection = Connection::open(&newer_path).expect("create newer database");
        connection
            .pragma_update(None, "application_id", APPLICATION_ID)
            .expect("set application ID");
        connection
            .pragma_update(None, "user_version", CURRENT_SCHEMA_VERSION + 1)
            .expect("set newer schema version");
        drop(connection);
        assert!(matches!(
            StateDatabase::open(&newer_path, identity()),
            Err(StorageError::UnsupportedSchemaVersion {
                found,
                supported
            }) if found == CURRENT_SCHEMA_VERSION + 1 && supported == CURRENT_SCHEMA_VERSION
        ));
    }

    #[test]
    fn rejects_a_database_bound_to_another_workspace() {
        let directory = TestDirectory::new("identity");
        let path = directory.database_path();
        StateDatabase::open(&path, identity()).expect("initialize database");

        let different = StateDatabaseIdentity {
            workspace_id: WorkspaceId::generate(),
            ..identity()
        };
        assert!(matches!(
            StateDatabase::open(path, different),
            Err(StorageError::IdentityMismatch { .. })
        ));
    }

    #[test]
    fn failed_migration_rolls_back_schema_header_and_objects() {
        let directory = TestDirectory::new("migration-rollback");
        let path = directory.database_path();
        let broken = [
            MIGRATIONS[0],
            Migration {
                version: 2,
                name: "broken",
                sql: "CREATE TABLE partial (value TEXT); THIS IS NOT SQL;",
            },
            MIGRATIONS[2],
            MIGRATIONS[3],
            MIGRATIONS[4],
        ];

        assert!(matches!(
            StateDatabase::open_with_migrations(&path, identity(), &broken),
            Err(StorageError::Sqlite(_))
        ));
        let connection = Connection::open(path).expect("inspect rolled back database");
        assert_eq!(pragma_i64_for_connection(&connection, "application_id"), 0);
        assert_eq!(pragma_i64_for_connection(&connection, "user_version"), 0);
        let partial: u32 = connection
            .query_row(
                "SELECT count(*) FROM sqlite_schema WHERE name = 'partial'",
                [],
                |row| row.get(0),
            )
            .expect("inspect partial table");
        assert_eq!(partial, 0);
    }

    #[test]
    fn failed_transaction_is_rolled_back() {
        let directory = TestDirectory::new("transaction-rollback");
        let path = directory.database_path();
        let mut database = StateDatabase::open(&path, identity()).expect("initialize database");

        let result = database.transaction(|transaction| {
            transaction
                .execute(
                    "UPDATE host_identity SET host_triple = 'changed' WHERE singleton = 1",
                    [],
                )
                .expect("update inside transaction");
            Err::<(), _>(InjectedFailure)
        });
        assert!(matches!(
            result,
            Err(TransactionError::Operation(InjectedFailure))
        ));
        drop(database);

        StateDatabase::open(path, identity()).expect("identity update was rolled back");
    }

    #[test]
    fn inserts_plugin_packages_idempotently_and_rejects_version_reuse() {
        let directory = TestDirectory::new("plugin-package");
        let path = directory.database_path();
        let mut database = StateDatabase::open(&path, identity()).expect("initialize database");
        let package = InstalledPluginPackage {
            plugin_id: "dev.softpilot.fixture".to_owned(),
            version: "1.0.0".to_owned(),
            package_sha256: "a".repeat(64),
            package_size_bytes: 42,
            relative_path: "plugins/packages/dev.softpilot.fixture/1.0.0/package.softpilot-plugin"
                .to_owned(),
            manifest_json: "{\"id\":\"dev.softpilot.fixture\"}".to_owned(),
            component_validated: false,
            installed_at_unix_seconds: 1_700_000_001,
        };

        assert_eq!(
            database
                .insert_plugin_package(&package)
                .expect("insert package"),
            InsertPluginPackageOutcome::Inserted
        );
        assert_eq!(
            database
                .insert_plugin_package(&package)
                .expect("repeat package"),
            InsertPluginPackageOutcome::AlreadyPresent
        );
        assert_eq!(
            database
                .plugin_package(&package.plugin_id, &package.version)
                .expect("read package"),
            Some(package.clone())
        );
        assert_eq!(
            database
                .latest_plugin_manifest(&package.plugin_id)
                .expect("read latest manifest"),
            Some(package.manifest_json.clone())
        );

        let conflict = InstalledPluginPackage {
            package_sha256: "b".repeat(64),
            ..package
        };
        assert!(matches!(
            database.insert_plugin_package(&conflict),
            Err(StorageError::PluginVersionConflict { .. })
        ));
    }

    #[test]
    fn lists_activates_switches_and_disables_plugin_versions() {
        let directory = TestDirectory::new("plugin-activation");
        let path = directory.database_path();
        let mut database = StateDatabase::open(&path, identity()).expect("initialize database");
        let first = InstalledPluginPackage {
            plugin_id: "dev.softpilot.fixture".to_owned(),
            version: "1.0.0".to_owned(),
            package_sha256: "a".repeat(64),
            package_size_bytes: 42,
            relative_path: "plugins/packages/dev.softpilot.fixture/1.0.0/package.softpilot-plugin"
                .to_owned(),
            manifest_json: "{\"id\":\"dev.softpilot.fixture\"}".to_owned(),
            component_validated: false,
            installed_at_unix_seconds: 1_700_000_001,
        };
        let second = InstalledPluginPackage {
            version: "2.0.0".to_owned(),
            package_sha256: "b".repeat(64),
            relative_path: "plugins/packages/dev.softpilot.fixture/2.0.0/package.softpilot-plugin"
                .to_owned(),
            installed_at_unix_seconds: 1_700_000_002,
            ..first.clone()
        };
        database
            .insert_plugin_package(&first)
            .expect("insert first package");
        database
            .insert_plugin_package(&second)
            .expect("insert second package");

        assert_eq!(
            database.plugin_packages().expect("list plugin packages"),
            vec![first.clone(), second.clone()]
        );
        assert_eq!(
            database
                .active_plugin_version(&first.plugin_id)
                .expect("read disabled state"),
            None
        );
        assert_eq!(
            database
                .activate_plugin(&first.plugin_id, &first.version, 1_700_000_003)
                .expect("activate first version"),
            ActivatePluginOutcome::Activated
        );
        assert_eq!(
            database
                .activate_plugin(&first.plugin_id, &first.version, 1_700_000_004)
                .expect("repeat first version"),
            ActivatePluginOutcome::AlreadyActive
        );
        assert_eq!(
            database
                .activate_plugin(&second.plugin_id, &second.version, 1_700_000_005)
                .expect("switch active version"),
            ActivatePluginOutcome::Activated
        );
        assert_eq!(
            database
                .active_plugin_version(&second.plugin_id)
                .expect("read active version"),
            Some(second.version.clone())
        );
        assert_eq!(
            database
                .disable_plugin(&second.plugin_id)
                .expect("disable plugin"),
            DisablePluginOutcome::Disabled
        );
        assert_eq!(
            database
                .disable_plugin(&second.plugin_id)
                .expect("repeat disable"),
            DisablePluginOutcome::AlreadyDisabled
        );
        assert!(matches!(
            database.activate_plugin("dev.softpilot.missing", "1.0.0", 1_700_000_006),
            Err(StorageError::Sqlite(_))
        ));
    }

    #[test]
    fn moves_inactive_plugin_packages_to_trash_and_restores_them() {
        let directory = TestDirectory::new("plugin-trash");
        let path = directory.database_path();
        let mut database = StateDatabase::open(&path, identity()).expect("initialize database");
        let package = InstalledPluginPackage {
            plugin_id: "dev.softpilot.trash".to_owned(),
            version: "1.0.0".to_owned(),
            package_sha256: "c".repeat(64),
            package_size_bytes: 43,
            relative_path: "plugins/packages/dev.softpilot.trash/1.0.0/package.softpilot-plugin"
                .to_owned(),
            manifest_json: "{\"id\":\"dev.softpilot.trash\"}".to_owned(),
            component_validated: false,
            installed_at_unix_seconds: 1_700_000_010,
        };
        database
            .insert_plugin_package(&package)
            .expect("insert package");
        database
            .activate_plugin(&package.plugin_id, &package.version, 1_700_000_011)
            .expect("activate package");
        let trashed = TrashedPluginPackage {
            trash_id: "trash-id".to_owned(),
            package: package.clone(),
            trash_relative_path: "plugins/trash/trash-id/package.softpilot-plugin".to_owned(),
            trashed_at_unix_seconds: 1_700_000_012,
        };
        assert!(matches!(
            database.trash_plugin_package(&trashed),
            Err(StorageError::Sqlite(_))
        ));
        assert_eq!(
            database
                .plugin_package(&package.plugin_id, &package.version)
                .expect("read package after rejected trash"),
            Some(package.clone())
        );

        database
            .disable_plugin(&package.plugin_id)
            .expect("disable package");
        database
            .trash_plugin_package(&trashed)
            .expect("move package state to trash");
        assert_eq!(
            database
                .plugin_package(&package.plugin_id, &package.version)
                .expect("read removed package"),
            None
        );
        assert_eq!(
            database
                .trashed_plugin_package(&package.plugin_id, &package.version)
                .expect("read trash entry"),
            Some(trashed.clone())
        );
        assert_eq!(
            database
                .trashed_plugin_packages()
                .expect("list trash entries"),
            vec![trashed]
        );

        let restored = database
            .restore_plugin_package(&package.plugin_id, &package.version)
            .expect("restore package state");
        assert_eq!(restored, package);
        assert!(
            database
                .trashed_plugin_packages()
                .expect("list empty trash")
                .is_empty()
        );
    }

    #[test]
    fn journals_and_atomically_completes_plugin_file_operations() {
        let directory = TestDirectory::new("plugin-operation-journal");
        let path = directory.database_path();
        let mut database = StateDatabase::open(&path, identity()).expect("initialize database");
        let package = InstalledPluginPackage {
            plugin_id: "dev.softpilot.journal".to_owned(),
            version: "1.0.0".to_owned(),
            package_sha256: "d".repeat(64),
            package_size_bytes: 44,
            relative_path: "plugins/packages/dev.softpilot.journal/1.0.0/package.softpilot-plugin"
                .to_owned(),
            manifest_json: "{\"id\":\"dev.softpilot.journal\"}".to_owned(),
            component_validated: false,
            installed_at_unix_seconds: 1_700_000_020,
        };
        let install = PendingPluginFileOperation {
            operation_id: "install-operation".to_owned(),
            kind: PluginFileOperationKind::Install,
            package: package.clone(),
            source_relative_directory: "plugins/staging/install-operation".to_owned(),
            destination_relative_directory: "plugins/packages/dev.softpilot.journal/1.0.0"
                .to_owned(),
            trash_id: None,
            trash_relative_path: None,
            operation_at_unix_seconds: 1_700_000_020,
        };
        database
            .begin_plugin_file_operation(&install)
            .expect("journal install");
        assert_eq!(
            database
                .pending_plugin_file_operations()
                .expect("list install journal"),
            vec![install.clone()]
        );
        database
            .complete_plugin_file_operation(&install)
            .expect("complete install state");
        assert_eq!(
            database
                .plugin_package(&package.plugin_id, &package.version)
                .expect("read installed journal package"),
            Some(package.clone())
        );
        assert!(
            database
                .pending_plugin_file_operations()
                .expect("empty journal after install")
                .is_empty()
        );

        let trash = PendingPluginFileOperation {
            operation_id: "trash-operation".to_owned(),
            kind: PluginFileOperationKind::Trash,
            package: package.clone(),
            source_relative_directory: install.destination_relative_directory.clone(),
            destination_relative_directory: "plugins/trash/trash-id".to_owned(),
            trash_id: Some("trash-id".to_owned()),
            trash_relative_path: Some("plugins/trash/trash-id/package.softpilot-plugin".to_owned()),
            operation_at_unix_seconds: 1_700_000_021,
        };
        database
            .begin_plugin_file_operation(&trash)
            .expect("journal trash");
        database
            .complete_plugin_file_operation(&trash)
            .expect("complete trash state");
        assert_eq!(
            database
                .trashed_plugin_package(&package.plugin_id, &package.version)
                .expect("read journal trash")
                .expect("trashed package")
                .trash_id,
            "trash-id"
        );

        let restore = PendingPluginFileOperation {
            operation_id: "restore-operation".to_owned(),
            kind: PluginFileOperationKind::Restore,
            source_relative_directory: trash.destination_relative_directory.clone(),
            destination_relative_directory: trash.source_relative_directory.clone(),
            ..trash
        };
        database
            .begin_plugin_file_operation(&restore)
            .expect("journal restore");
        database
            .complete_plugin_file_operation(&restore)
            .expect("complete restore state");
        assert_eq!(
            database
                .plugin_package(&package.plugin_id, &package.version)
                .expect("read restored journal package"),
            Some(package)
        );
    }

    #[test]
    fn cancels_a_plugin_file_operation_journal() {
        let directory = TestDirectory::new("plugin-operation-cancel");
        let path = directory.database_path();
        let mut database = StateDatabase::open(&path, identity()).expect("initialize database");
        let cancel = PendingPluginFileOperation {
            operation_id: "cancel-operation".to_owned(),
            kind: PluginFileOperationKind::Install,
            package: InstalledPluginPackage {
                plugin_id: "dev.softpilot.cancel".to_owned(),
                version: "1.0.0".to_owned(),
                package_sha256: "e".repeat(64),
                package_size_bytes: 45,
                relative_path:
                    "plugins/packages/dev.softpilot.cancel/1.0.0/package.softpilot-plugin"
                        .to_owned(),
                manifest_json: "{\"id\":\"dev.softpilot.cancel\"}".to_owned(),
                component_validated: false,
                installed_at_unix_seconds: 1_700_000_022,
            },
            source_relative_directory: "plugins/staging/cancel-operation".to_owned(),
            destination_relative_directory: "plugins/packages/dev.softpilot.cancel/1.0.0"
                .to_owned(),
            trash_id: None,
            trash_relative_path: None,
            operation_at_unix_seconds: 1_700_000_022,
        };
        database
            .begin_plugin_file_operation(&cancel)
            .expect("journal cancellation");
        database
            .cancel_plugin_file_operation(&cancel.operation_id)
            .expect("cancel journal");
        assert!(
            database
                .pending_plugin_file_operations()
                .expect("empty journal after cancellation")
                .is_empty()
        );
    }

    fn pragma_i64_for_connection(connection: &Connection, name: &str) -> i64 {
        connection
            .pragma_query_value(None, name, |row| row.get(0))
            .expect("read integer pragma")
    }
}
