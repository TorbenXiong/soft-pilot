use std::{
    fs::{self, File},
    io,
    path::{Path, PathBuf},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use serde::Serialize;
use softpilot_core::{PluginId, PluginIdError};
use softpilot_plugin_api::{
    CompatibilityContext, CompatibilityError, PackageError, PluginManifest, PluginPackageInspector,
    PluginPermissionsDiff,
};
use softpilot_storage::{
    ActivatePluginOutcome, DisablePluginOutcome, InstalledPluginPackage,
    InstalledPluginPackageState, PendingPluginFileOperation, PluginFileOperationKind,
    StateDatabase, StateDatabaseIdentity, StorageError, TrashedPluginPackage,
};
use thiserror::Error;
use uuid::Uuid;

use crate::{WorkspaceError, WorkspaceInfo, WorkspaceService};

const PACKAGE_FILE_NAME: &str = "package.softpilot-plugin";
const INSTALL_LOCK_TIMEOUT: Duration = Duration::from_secs(30);

/// Shared plugin installation use case.
#[derive(Debug, Clone)]
pub struct PluginService {
    workspaces: WorkspaceService,
}

impl PluginService {
    /// Creates a plugin service using the same workspace locator and lock service as the caller.
    #[must_use]
    pub const fn new(workspaces: WorkspaceService) -> Self {
        Self { workspaces }
    }

    /// Validates, stages, and atomically commits one immutable plugin package.
    ///
    /// Added permissions must be explicitly accepted. Package bytes are inspected before the
    /// workspace is changed and again after copying into staging.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] without replacing an existing plugin version or bypassing
    /// compatibility, digest, permission, workspace lock, or state database checks.
    pub fn install(
        &self,
        workspace: &WorkspaceInfo,
        package_path: impl AsRef<Path>,
        accept_permissions: bool,
    ) -> Result<PluginInstallResult, PluginInstallError> {
        let package_path = package_path.as_ref();
        let inspected = PluginPackageInspector::inspect(package_path)?;
        let compatibility = CompatibilityContext::current()?;
        inspected.manifest.ensure_compatible(&compatibility)?;

        let _lock = self.workspaces.acquire_lock(
            &workspace.path,
            "plugin.install",
            INSTALL_LOCK_TIMEOUT,
        )?;
        let mut database = open_state_database(workspace)?;
        install_locked(
            workspace,
            package_path,
            inspected,
            &compatibility,
            &mut database,
            accept_permissions,
        )
    }

    /// Lists installed plugin versions and their active state.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] if state cannot be read or contains invalid plugin metadata.
    pub fn list(
        &self,
        workspace: &WorkspaceInfo,
    ) -> Result<Vec<PluginPackageStatus>, PluginInstallError> {
        let _lock =
            self.workspaces
                .acquire_lock(&workspace.path, "plugin.list", INSTALL_LOCK_TIMEOUT)?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)?;
        load_plugin_statuses(&database, workspace)
    }

    /// Enables an exact installed version, or the highest installed semantic version when omitted.
    ///
    /// The immutable package is revalidated before its active state changes.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] for invalid IDs, missing versions, unsafe persisted metadata,
    /// package changes, incompatibility, lock failures, or state update failures.
    pub fn enable(
        &self,
        workspace: &WorkspaceInfo,
        plugin_id: &str,
        version: Option<&str>,
    ) -> Result<PluginActivationResult, PluginInstallError> {
        let plugin_id: PluginId = plugin_id.parse()?;
        let _lock =
            self.workspaces
                .acquire_lock(&workspace.path, "plugin.enable", INSTALL_LOCK_TIMEOUT)?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)?;
        let selected = select_plugin_version(
            load_plugin_statuses(&database, workspace)?,
            &plugin_id,
            version,
        )?;
        validate_committed_package(&selected)?;
        selected
            .manifest
            .ensure_compatible(&CompatibilityContext::current()?)?;
        let outcome =
            database.activate_plugin(plugin_id.as_str(), &selected.version, unix_timestamp()?)?;
        Ok(PluginActivationResult {
            plugin_id: plugin_id.to_string(),
            active_version: Some(selected.version),
            changed: outcome == ActivatePluginOutcome::Activated,
        })
    }

    /// Disables a plugin without deleting any installed version.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] for invalid IDs, unknown plugins, lock failures, or state
    /// update failures.
    pub fn disable(
        &self,
        workspace: &WorkspaceInfo,
        plugin_id: &str,
    ) -> Result<PluginActivationResult, PluginInstallError> {
        let plugin_id: PluginId = plugin_id.parse()?;
        let _lock = self.workspaces.acquire_lock(
            &workspace.path,
            "plugin.disable",
            INSTALL_LOCK_TIMEOUT,
        )?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)?;
        if !database
            .plugin_package_states()?
            .iter()
            .any(|state| state.package.plugin_id == plugin_id.as_str())
        {
            return Err(PluginInstallError::PluginNotInstalled(
                plugin_id.to_string(),
            ));
        }
        let outcome = database.disable_plugin(plugin_id.as_str())?;
        Ok(PluginActivationResult {
            plugin_id: plugin_id.to_string(),
            active_version: None,
            changed: outcome == DisablePluginOutcome::Disabled,
        })
    }

    /// Moves an inactive installed plugin version into recoverable workspace trash.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] for invalid IDs, active or missing versions, package changes,
    /// unsafe paths, lock failures, or state/rollback failures.
    pub fn uninstall(
        &self,
        workspace: &WorkspaceInfo,
        plugin_id: &str,
        version: &str,
    ) -> Result<PluginTrashResult, PluginInstallError> {
        let plugin_id: PluginId = plugin_id.parse()?;
        let _lock = self.workspaces.acquire_lock(
            &workspace.path,
            "plugin.uninstall",
            INSTALL_LOCK_TIMEOUT,
        )?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)?;
        if database
            .active_plugin_version(plugin_id.as_str())?
            .as_deref()
            == Some(version)
        {
            return Err(PluginInstallError::ActivePluginCannotUninstall {
                plugin_id: plugin_id.to_string(),
                version: version.to_owned(),
            });
        }
        let Some(package) = database.plugin_package(plugin_id.as_str(), version)? else {
            return already_trashed_result(&database, workspace, &plugin_id, version);
        };
        let status = plugin_status(
            workspace,
            InstalledPluginPackageState {
                package: package.clone(),
                active: false,
            },
        )?;
        validate_committed_package(&status)?;
        move_package_to_trash(workspace, &mut database, package, &status)
    }

    /// Lists recoverable plugin package trash entries.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] if state cannot be read or contains unsafe metadata.
    pub fn trash(
        &self,
        workspace: &WorkspaceInfo,
    ) -> Result<Vec<PluginTrashStatus>, PluginInstallError> {
        let _lock =
            self.workspaces
                .acquire_lock(&workspace.path, "plugin.trash", INSTALL_LOCK_TIMEOUT)?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)?;
        database
            .trashed_plugin_packages()?
            .into_iter()
            .map(|trashed| plugin_trash_status(workspace, trashed))
            .collect()
    }

    /// Restores a trashed plugin version without enabling it.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] for invalid IDs, missing or changed trash entries, destination
    /// conflicts, lock failures, or state/rollback failures.
    pub fn restore(
        &self,
        workspace: &WorkspaceInfo,
        plugin_id: &str,
        version: &str,
    ) -> Result<PluginTrashResult, PluginInstallError> {
        let plugin_id: PluginId = plugin_id.parse()?;
        let _lock = self.workspaces.acquire_lock(
            &workspace.path,
            "plugin.restore",
            INSTALL_LOCK_TIMEOUT,
        )?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)?;
        let Some(trashed) = database.trashed_plugin_package(plugin_id.as_str(), version)? else {
            if let Some(package) = database.plugin_package(plugin_id.as_str(), version)? {
                let status = plugin_status(
                    workspace,
                    InstalledPluginPackageState {
                        package,
                        active: false,
                    },
                )?;
                return Ok(PluginTrashResult {
                    plugin_id: plugin_id.to_string(),
                    version: version.to_owned(),
                    package_path: status.package_path,
                    changed: false,
                });
            }
            return Err(PluginInstallError::TrashedPluginVersionNotFound {
                plugin_id: plugin_id.to_string(),
                version: version.to_owned(),
            });
        };
        restore_package_from_trash(workspace, &mut database, trashed)
    }

    /// Reconciles durable plugin file-operation journals after an interrupted process.
    ///
    /// # Errors
    ///
    /// Returns [`PluginInstallError`] when journal metadata or filesystem state is unsafe or
    /// ambiguous. No safety check is bypassed.
    pub fn recover(
        &self,
        workspace: &WorkspaceInfo,
    ) -> Result<PluginRecoveryResult, PluginInstallError> {
        let _lock = self.workspaces.acquire_lock(
            &workspace.path,
            "plugin.recover",
            INSTALL_LOCK_TIMEOUT,
        )?;
        let mut database = open_state_database(workspace)?;
        recover_pending_operations(workspace, &mut database)
    }
}

fn load_plugin_statuses(
    database: &StateDatabase,
    workspace: &WorkspaceInfo,
) -> Result<Vec<PluginPackageStatus>, PluginInstallError> {
    database
        .plugin_package_states()?
        .into_iter()
        .map(|state| plugin_status(workspace, state))
        .collect()
}

fn plugin_status(
    workspace: &WorkspaceInfo,
    state: InstalledPluginPackageState,
) -> Result<PluginPackageStatus, PluginInstallError> {
    let manifest = PluginManifest::from_slice(state.package.manifest_json.as_bytes())?;
    let destination = InstallDestination::new(workspace, &manifest);
    if state.package.plugin_id != destination.plugin_id
        || state.package.version != destination.version
        || state.package.relative_path != destination.relative_path
    {
        return Err(PluginInstallError::StoredPackageMetadataMismatch {
            plugin_id: state.package.plugin_id,
            version: state.package.version,
        });
    }
    Ok(PluginPackageStatus {
        plugin_id: destination.plugin_id,
        version: destination.version,
        name: manifest.name.clone(),
        manifest,
        package_path: destination.final_package,
        package_size_bytes: state.package.package_size_bytes,
        package_sha256: state.package.package_sha256,
        component_validated: state.package.component_validated,
        installed_at_unix_seconds: state.package.installed_at_unix_seconds,
        active: state.active,
    })
}

fn select_plugin_version(
    statuses: Vec<PluginPackageStatus>,
    plugin_id: &PluginId,
    version: Option<&str>,
) -> Result<PluginPackageStatus, PluginInstallError> {
    let mut candidates = statuses
        .into_iter()
        .filter(|status| status.plugin_id == plugin_id.as_str());
    if let Some(version) = version {
        return candidates
            .find(|status| status.version == version)
            .ok_or_else(|| PluginInstallError::PluginVersionNotInstalled {
                plugin_id: plugin_id.to_string(),
                version: version.to_owned(),
            });
    }
    candidates
        .max_by(|left, right| left.manifest.version.cmp(&right.manifest.version))
        .ok_or_else(|| PluginInstallError::PluginNotInstalled(plugin_id.to_string()))
}

fn validate_committed_package(status: &PluginPackageStatus) -> Result<(), PluginInstallError> {
    let inspected = PluginPackageInspector::inspect(&status.package_path).map_err(|source| {
        PluginInstallError::CommittedPackageInvalid {
            path: status.package_path.clone(),
            source,
        }
    })?;
    if inspected.package_sha256 != status.package_sha256
        || inspected.package_size_bytes != status.package_size_bytes
        || inspected.manifest != status.manifest
        || inspected.component_validated != status.component_validated
    {
        return Err(PluginInstallError::CommittedPackageChanged(
            status.package_path.clone(),
        ));
    }
    Ok(())
}

fn recover_pending_operations(
    workspace: &WorkspaceInfo,
    database: &mut StateDatabase,
) -> Result<PluginRecoveryResult, PluginInstallError> {
    let operations = database.pending_plugin_file_operations()?;
    let mut result = PluginRecoveryResult::default();
    for operation in operations {
        let paths = pending_operation_paths(workspace, &operation)?;
        let source_exists = real_directory_exists(&paths.source)?;
        let destination_exists = real_directory_exists(&paths.destination)?;
        match (source_exists, destination_exists) {
            (true, false) => {
                validate_operation_package(&paths.source, &operation)?;
                database.cancel_plugin_file_operation(&operation.operation_id)?;
                if operation.kind == PluginFileOperationKind::Install {
                    fs::remove_dir_all(&paths.source).map_err(|source| PluginInstallError::Io {
                        operation: "remove interrupted plugin install staging directory",
                        path: paths.source,
                        source,
                    })?;
                }
                result.cancelled += 1;
            }
            (false, true) => {
                validate_operation_package(&paths.destination, &operation)?;
                database.complete_plugin_file_operation(&operation)?;
                result.completed += 1;
            }
            _ => {
                return Err(PluginInstallError::AmbiguousRecoveryState {
                    operation_id: operation.operation_id,
                    source_path: paths.source,
                    destination_path: paths.destination,
                    source_exists,
                    destination_exists,
                });
            }
        }
    }
    Ok(result)
}

struct PendingOperationPaths {
    source: PathBuf,
    destination: PathBuf,
}

fn pending_operation_paths(
    workspace: &WorkspaceInfo,
    operation: &PendingPluginFileOperation,
) -> Result<PendingOperationPaths, PluginInstallError> {
    let manifest = PluginManifest::from_slice(operation.package.manifest_json.as_bytes())?;
    let installed = InstallDestination::new(workspace, &manifest);
    let operation_id_valid = Uuid::parse_str(&operation.operation_id)
        .is_ok_and(|value| value.to_string() == operation.operation_id);
    if !operation_id_valid
        || operation.package.plugin_id != installed.plugin_id
        || operation.package.version != installed.version
        || operation.package.relative_path != installed.relative_path
    {
        return Err(PluginInstallError::InvalidRecoveryJournal(
            operation.operation_id.clone(),
        ));
    }
    let installed_relative = installed.relative_directory.clone();
    let staging_relative = format!("plugins/staging/install-{}", operation.operation_id);
    let (source_relative, destination_relative) = match operation.kind {
        PluginFileOperationKind::Install => {
            if operation.source_relative_directory != staging_relative
                || operation.destination_relative_directory != installed_relative
                || operation.trash_id.is_some()
                || operation.trash_relative_path.is_some()
            {
                return Err(PluginInstallError::InvalidRecoveryJournal(
                    operation.operation_id.clone(),
                ));
            }
            (staging_relative, installed_relative)
        }
        PluginFileOperationKind::Trash | PluginFileOperationKind::Restore => {
            let trash_id = operation.trash_id.as_deref().ok_or_else(|| {
                PluginInstallError::InvalidRecoveryJournal(operation.operation_id.clone())
            })?;
            let valid_trash_id =
                Uuid::parse_str(trash_id).is_ok_and(|value| value.to_string() == trash_id);
            let trash_relative = format!("plugins/trash/{trash_id}");
            let expected_package = format!("{trash_relative}/{PACKAGE_FILE_NAME}");
            if !valid_trash_id
                || operation.trash_relative_path.as_deref() != Some(expected_package.as_str())
            {
                return Err(PluginInstallError::InvalidRecoveryJournal(
                    operation.operation_id.clone(),
                ));
            }
            if operation.kind == PluginFileOperationKind::Trash {
                (installed_relative, trash_relative)
            } else {
                (trash_relative, installed_relative)
            }
        }
    };
    if operation.source_relative_directory != source_relative
        || operation.destination_relative_directory != destination_relative
    {
        return Err(PluginInstallError::InvalidRecoveryJournal(
            operation.operation_id.clone(),
        ));
    }
    Ok(PendingOperationPaths {
        source: workspace.path.as_path().join(source_relative),
        destination: workspace.path.as_path().join(destination_relative),
    })
}

fn real_directory_exists(path: &Path) -> Result<bool, PluginInstallError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_dir() => Ok(true),
        Ok(_) => Err(PluginInstallError::UnsafeDirectory(path.to_owned())),
        Err(source) if source.kind() == io::ErrorKind::NotFound => Ok(false),
        Err(source) => Err(PluginInstallError::Io {
            operation: "inspect a plugin recovery directory",
            path: path.to_owned(),
            source,
        }),
    }
}

fn validate_operation_package(
    directory: &Path,
    operation: &PendingPluginFileOperation,
) -> Result<(), PluginInstallError> {
    let package_path = directory.join(PACKAGE_FILE_NAME);
    let inspected = PluginPackageInspector::inspect(&package_path).map_err(|source| {
        PluginInstallError::CommittedPackageInvalid {
            path: package_path.clone(),
            source,
        }
    })?;
    let persisted_manifest =
        PluginManifest::from_slice(operation.package.manifest_json.as_bytes())?;
    if inspected.package_sha256 != operation.package.package_sha256
        || inspected.package_size_bytes != operation.package.package_size_bytes
        || inspected.manifest != persisted_manifest
        || inspected.component_validated != operation.package.component_validated
    {
        return Err(PluginInstallError::CommittedPackageChanged(package_path));
    }
    Ok(())
}

fn already_trashed_result(
    database: &StateDatabase,
    workspace: &WorkspaceInfo,
    plugin_id: &PluginId,
    version: &str,
) -> Result<PluginTrashResult, PluginInstallError> {
    let trashed = database
        .trashed_plugin_package(plugin_id.as_str(), version)?
        .ok_or_else(|| PluginInstallError::PluginVersionNotInstalled {
            plugin_id: plugin_id.to_string(),
            version: version.to_owned(),
        })?;
    let status = plugin_trash_status(workspace, trashed)?;
    validate_trashed_package(&status)?;
    Ok(PluginTrashResult {
        plugin_id: status.plugin_id,
        version: status.version,
        package_path: status.package_path,
        changed: false,
    })
}

fn move_package_to_trash(
    workspace: &WorkspaceInfo,
    database: &mut StateDatabase,
    package: InstalledPluginPackage,
    status: &PluginPackageStatus,
) -> Result<PluginTrashResult, PluginInstallError> {
    let trash_root = workspace.path.as_path().join("plugins/trash");
    validate_real_directory(&trash_root)?;
    let trash_id = Uuid::new_v4().to_string();
    let trash_directory = trash_root.join(&trash_id);
    let trash_relative_path = format!("plugins/trash/{trash_id}/{PACKAGE_FILE_NAME}");
    let trashed_at_unix_seconds = unix_timestamp()?;
    let destination = InstallDestination::new(workspace, &status.manifest);
    let trashed = TrashedPluginPackage {
        trash_id: trash_id.clone(),
        package,
        trash_relative_path: trash_relative_path.clone(),
        trashed_at_unix_seconds,
    };
    let operation = PendingPluginFileOperation {
        operation_id: Uuid::new_v4().to_string(),
        kind: PluginFileOperationKind::Trash,
        package: trashed.package.clone(),
        source_relative_directory: destination.relative_directory.clone(),
        destination_relative_directory: format!("plugins/trash/{trash_id}"),
        trash_id: Some(trash_id),
        trash_relative_path: Some(trash_relative_path),
        operation_at_unix_seconds: trashed_at_unix_seconds,
    };
    database.begin_plugin_file_operation(&operation)?;
    if let Err(source) = fs::rename(&destination.final_directory, &trash_directory) {
        database.cancel_plugin_file_operation(&operation.operation_id)?;
        return Err(PluginInstallError::Io {
            operation: "move plugin version directory to trash",
            path: destination.final_directory.clone(),
            source,
        });
    }
    if let Err(storage) = database.complete_plugin_file_operation(&operation) {
        if let Err(rollback) = fs::rename(&trash_directory, &destination.final_directory) {
            return Err(PluginInstallError::TrashStateRollbackFailed {
                path: trash_directory,
                storage: Box::new(storage),
                rollback: Box::new(rollback),
            });
        }
        database.cancel_plugin_file_operation(&operation.operation_id)?;
        return Err(storage.into());
    }
    Ok(PluginTrashResult {
        plugin_id: trashed.package.plugin_id,
        version: trashed.package.version,
        package_path: trash_directory.join(PACKAGE_FILE_NAME),
        changed: true,
    })
}

fn plugin_trash_status(
    workspace: &WorkspaceInfo,
    trashed: TrashedPluginPackage,
) -> Result<PluginTrashStatus, PluginInstallError> {
    let manifest = PluginManifest::from_slice(trashed.package.manifest_json.as_bytes())?;
    let original = InstallDestination::new(workspace, &manifest);
    let valid_trash_id =
        Uuid::parse_str(&trashed.trash_id).is_ok_and(|value| value.to_string() == trashed.trash_id);
    let expected_relative = format!("plugins/trash/{}/{PACKAGE_FILE_NAME}", trashed.trash_id);
    if !valid_trash_id
        || trashed.package.plugin_id != original.plugin_id
        || trashed.package.version != original.version
        || trashed.package.relative_path != original.relative_path
        || trashed.trash_relative_path != expected_relative
    {
        return Err(PluginInstallError::StoredTrashMetadataMismatch {
            plugin_id: trashed.package.plugin_id,
            version: trashed.package.version,
        });
    }
    let trash_directory = workspace
        .path
        .as_path()
        .join("plugins/trash")
        .join(&trashed.trash_id);
    Ok(PluginTrashStatus {
        trash_id: trashed.trash_id,
        plugin_id: original.plugin_id,
        version: original.version,
        name: manifest.name.clone(),
        manifest,
        package_path: trash_directory.join(PACKAGE_FILE_NAME),
        package_size_bytes: trashed.package.package_size_bytes,
        package_sha256: trashed.package.package_sha256,
        component_validated: trashed.package.component_validated,
        installed_at_unix_seconds: trashed.package.installed_at_unix_seconds,
        trashed_at_unix_seconds: trashed.trashed_at_unix_seconds,
    })
}

fn validate_trashed_package(status: &PluginTrashStatus) -> Result<(), PluginInstallError> {
    let directory = status
        .package_path
        .parent()
        .ok_or_else(|| PluginInstallError::UnsafeDirectory(status.package_path.clone()))?;
    validate_real_directory(directory)?;
    let inspected = PluginPackageInspector::inspect(&status.package_path).map_err(|source| {
        PluginInstallError::CommittedPackageInvalid {
            path: status.package_path.clone(),
            source,
        }
    })?;
    if inspected.package_sha256 != status.package_sha256
        || inspected.package_size_bytes != status.package_size_bytes
        || inspected.manifest != status.manifest
        || inspected.component_validated != status.component_validated
    {
        return Err(PluginInstallError::CommittedPackageChanged(
            status.package_path.clone(),
        ));
    }
    Ok(())
}

fn restore_package_from_trash(
    workspace: &WorkspaceInfo,
    database: &mut StateDatabase,
    trashed: TrashedPluginPackage,
) -> Result<PluginTrashResult, PluginInstallError> {
    let status = plugin_trash_status(workspace, trashed.clone())?;
    validate_trashed_package(&status)?;
    status
        .manifest
        .ensure_compatible(&CompatibilityContext::current()?)?;
    let destination = InstallDestination::new(workspace, &status.manifest);
    if destination
        .final_directory
        .try_exists()
        .map_err(|source| PluginInstallError::Io {
            operation: "inspect plugin restore destination",
            path: destination.final_directory.clone(),
            source,
        })?
    {
        return Err(PluginInstallError::UntrackedVersionDirectory(
            destination.final_directory,
        ));
    }
    ensure_or_create_real_directory(&destination.package_host_directory)?;
    ensure_or_create_real_directory(&destination.package_id_directory)?;
    let trash_directory = status
        .package_path
        .parent()
        .ok_or_else(|| PluginInstallError::UnsafeDirectory(status.package_path.clone()))?
        .to_owned();
    let operation = PendingPluginFileOperation {
        operation_id: Uuid::new_v4().to_string(),
        kind: PluginFileOperationKind::Restore,
        package: trashed.package,
        source_relative_directory: format!("plugins/trash/{}", trashed.trash_id),
        destination_relative_directory: destination.relative_directory.clone(),
        trash_id: Some(trashed.trash_id),
        trash_relative_path: Some(trashed.trash_relative_path),
        operation_at_unix_seconds: trashed.trashed_at_unix_seconds,
    };
    database.begin_plugin_file_operation(&operation)?;
    if let Err(source) = fs::rename(&trash_directory, &destination.final_directory) {
        database.cancel_plugin_file_operation(&operation.operation_id)?;
        return Err(PluginInstallError::Io {
            operation: "restore plugin version directory from trash",
            path: trash_directory.clone(),
            source,
        });
    }
    if let Err(storage) = database.complete_plugin_file_operation(&operation) {
        if let Err(rollback) = fs::rename(&destination.final_directory, &trash_directory) {
            return Err(PluginInstallError::TrashStateRollbackFailed {
                path: destination.final_directory,
                storage: Box::new(storage),
                rollback: Box::new(rollback),
            });
        }
        database.cancel_plugin_file_operation(&operation.operation_id)?;
        return Err(storage.into());
    }
    Ok(PluginTrashResult {
        plugin_id: status.plugin_id,
        version: status.version,
        package_path: destination.final_package,
        changed: true,
    })
}

fn install_locked(
    workspace: &WorkspaceInfo,
    package_path: &Path,
    inspected: softpilot_plugin_api::InspectedPlugin,
    compatibility: &CompatibilityContext,
    database: &mut StateDatabase,
    accept_permissions: bool,
) -> Result<PluginInstallResult, PluginInstallError> {
    recover_pending_operations(workspace, database)?;
    let previous_manifest = database
        .latest_plugin_manifest(inspected.manifest.id.as_str())?
        .map(|json| PluginManifest::from_slice(json.as_bytes()))
        .transpose()?;
    let permissions = PluginPermissionsDiff::between(
        previous_manifest
            .as_ref()
            .map(|manifest| &manifest.permissions),
        &inspected.manifest.permissions,
    );
    if permissions.requires_confirmation() && !accept_permissions {
        return Err(PluginInstallError::PermissionConfirmationRequired {
            difference: permissions,
        });
    }

    let destination = InstallDestination::new(workspace, &inspected.manifest);

    if let Some(existing) = database.plugin_package(&destination.plugin_id, &destination.version)? {
        ensure_existing_record_matches(&existing, &inspected.package_sha256)?;
        let committed =
            PluginPackageInspector::inspect(&destination.final_package).map_err(|source| {
                PluginInstallError::CommittedPackageInvalid {
                    path: destination.final_package.clone(),
                    source,
                }
            })?;
        ensure_staged_matches(&inspected, &committed)?;
        return Ok(PluginInstallResult {
            plugin_id: destination.plugin_id,
            version: destination.version,
            package_path: destination.final_package,
            package_size_bytes: inspected.package_size_bytes,
            package_sha256: inspected.package_sha256,
            component_validated: inspected.component_validated,
            installed: false,
            permissions,
        });
    }

    if destination
        .final_directory
        .try_exists()
        .map_err(|source| PluginInstallError::Io {
            operation: "inspect the final plugin version directory",
            path: destination.final_directory.clone(),
            source,
        })?
    {
        return Err(PluginInstallError::UntrackedVersionDirectory(
            destination.final_directory,
        ));
    }

    commit_new_package(
        workspace,
        package_path,
        &inspected,
        compatibility,
        permissions,
        database,
        destination,
    )
}

fn commit_new_package(
    workspace: &WorkspaceInfo,
    package_path: &Path,
    inspected: &softpilot_plugin_api::InspectedPlugin,
    compatibility: &CompatibilityContext,
    permissions: PluginPermissionsDiff,
    database: &mut StateDatabase,
    destination: InstallDestination,
) -> Result<PluginInstallResult, PluginInstallError> {
    let staging_root = workspace.path.as_path().join("plugins/staging");
    validate_real_directory(&staging_root)?;
    let operation_id = Uuid::new_v4().to_string();
    let staging_relative_directory = format!("plugins/staging/install-{operation_id}");
    let staging_directory = staging_root.join(format!("install-{operation_id}"));
    fs::create_dir(&staging_directory).map_err(|source| PluginInstallError::Io {
        operation: "create plugin staging directory",
        path: staging_directory.clone(),
        source,
    })?;
    let mut staging = StagingGuard::new(staging_directory);
    let staged_package = staging.path().join(PACKAGE_FILE_NAME);
    fs::copy(package_path, &staged_package).map_err(|source| PluginInstallError::Io {
        operation: "copy plugin package into staging",
        path: staged_package.clone(),
        source,
    })?;
    File::options()
        .read(true)
        .write(true)
        .open(&staged_package)
        .and_then(|file| file.sync_all())
        .map_err(|source| PluginInstallError::Io {
            operation: "flush the staged plugin package",
            path: staged_package.clone(),
            source,
        })?;

    let staged = PluginPackageInspector::inspect(&staged_package)?;
    staged.manifest.ensure_compatible(compatibility)?;
    ensure_staged_matches(inspected, &staged)?;

    let installed_at_unix_seconds = unix_timestamp()?;
    let record = InstalledPluginPackage {
        plugin_id: destination.plugin_id.clone(),
        version: destination.version.clone(),
        package_sha256: staged.package_sha256.clone(),
        package_size_bytes: staged.package_size_bytes,
        relative_path: destination.relative_path.clone(),
        manifest_json: serde_json::to_string(&staged.manifest)?,
        component_validated: staged.component_validated,
        installed_at_unix_seconds,
    };
    let operation = PendingPluginFileOperation {
        operation_id,
        kind: PluginFileOperationKind::Install,
        package: record,
        source_relative_directory: staging_relative_directory,
        destination_relative_directory: destination.relative_directory.clone(),
        trash_id: None,
        trash_relative_path: None,
        operation_at_unix_seconds: installed_at_unix_seconds,
    };
    ensure_or_create_real_directory(&destination.package_host_directory)?;
    ensure_or_create_real_directory(&destination.package_id_directory)?;
    database.begin_plugin_file_operation(&operation)?;
    if let Err(source) = fs::rename(staging.path(), &destination.final_directory) {
        if let Err(storage) = database.cancel_plugin_file_operation(&operation.operation_id) {
            staging.mark_moved();
            return Err(storage.into());
        }
        return Err(PluginInstallError::Io {
            operation: "atomically commit the plugin version directory",
            path: destination.final_directory.clone(),
            source,
        });
    }
    staging.mark_moved();

    match database.complete_plugin_file_operation(&operation) {
        Ok(()) => {}
        Err(storage) => {
            if let Err(rollback) = fs::rename(&destination.final_directory, staging.path()) {
                return Err(PluginInstallError::StateRollbackFailed {
                    final_path: destination.final_directory,
                    storage: Box::new(storage),
                    rollback: Box::new(rollback),
                });
            }
            if let Err(cancel) = database.cancel_plugin_file_operation(&operation.operation_id) {
                staging.mark_moved();
                return Err(cancel.into());
            }
            staging.mark_present();
            return Err(storage.into());
        }
    }

    Ok(PluginInstallResult {
        plugin_id: destination.plugin_id,
        version: destination.version,
        package_path: destination.final_package,
        package_size_bytes: staged.package_size_bytes,
        package_sha256: staged.package_sha256,
        component_validated: staged.component_validated,
        installed: true,
        permissions,
    })
}

struct InstallDestination {
    plugin_id: String,
    version: String,
    relative_directory: String,
    relative_path: String,
    package_host_directory: PathBuf,
    package_id_directory: PathBuf,
    final_directory: PathBuf,
    final_package: PathBuf,
}

impl InstallDestination {
    fn new(workspace: &WorkspaceInfo, manifest: &PluginManifest) -> Self {
        let plugin_id = manifest.id.as_str().to_owned();
        let version = manifest.version.to_string();
        let relative_directory = format!(
            "plugins/packages/{}/{plugin_id}/{version}",
            workspace.host_triple.as_str()
        );
        let relative_path = format!("{relative_directory}/{PACKAGE_FILE_NAME}");
        let package_host_directory = workspace
            .path
            .as_path()
            .join("plugins/packages")
            .join(workspace.host_triple.as_str());
        let package_id_directory = package_host_directory.join(&plugin_id);
        let final_directory = package_id_directory.join(&version);
        let final_package = final_directory.join(PACKAGE_FILE_NAME);
        Self {
            plugin_id,
            version,
            relative_directory,
            relative_path,
            package_host_directory,
            package_id_directory,
            final_directory,
            final_package,
        }
    }
}

fn open_state_database(workspace: &WorkspaceInfo) -> Result<StateDatabase, StorageError> {
    let path = workspace
        .path
        .as_path()
        .join("hosts")
        .join(workspace.host_triple.as_str())
        .join("data/state.db");
    StateDatabase::open(
        path,
        StateDatabaseIdentity::new(workspace.metadata, workspace.host_triple),
    )
}

fn unix_timestamp() -> Result<u64, PluginInstallError> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| PluginInstallError::ClockBeforeUnixEpoch)
        .map(|duration| duration.as_secs())
}

fn ensure_existing_record_matches(
    existing: &InstalledPluginPackage,
    requested_sha256: &str,
) -> Result<(), PluginInstallError> {
    if existing.package_sha256 == requested_sha256 {
        Ok(())
    } else {
        Err(PluginInstallError::VersionDigestConflict {
            plugin_id: existing.plugin_id.clone(),
            version: existing.version.clone(),
            existing_sha256: existing.package_sha256.clone(),
            requested_sha256: requested_sha256.to_owned(),
        })
    }
}

fn ensure_staged_matches(
    source: &softpilot_plugin_api::InspectedPlugin,
    staged: &softpilot_plugin_api::InspectedPlugin,
) -> Result<(), PluginInstallError> {
    if source.package_sha256 != staged.package_sha256
        || source.package_size_bytes != staged.package_size_bytes
    {
        return Err(PluginInstallError::StagedPackageChanged {
            source_sha256: source.package_sha256.clone(),
            staged_sha256: staged.package_sha256.clone(),
        });
    }
    if source.manifest != staged.manifest
        || source.component_validated != staged.component_validated
    {
        return Err(PluginInstallError::StagedValidationChanged);
    }
    Ok(())
}

fn validate_real_directory(path: &Path) -> Result<(), PluginInstallError> {
    let metadata = fs::symlink_metadata(path).map_err(|source| PluginInstallError::Io {
        operation: "validate plugin directory",
        path: path.to_owned(),
        source,
    })?;
    if metadata.file_type().is_dir() {
        Ok(())
    } else {
        Err(PluginInstallError::UnsafeDirectory(path.to_owned()))
    }
}

fn ensure_or_create_real_directory(path: &Path) -> Result<(), PluginInstallError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_dir() => Ok(()),
        Ok(_) => Err(PluginInstallError::UnsafeDirectory(path.to_owned())),
        Err(source) if source.kind() == io::ErrorKind::NotFound => {
            fs::create_dir(path).map_err(|source| PluginInstallError::Io {
                operation: "create plugin package directory",
                path: path.to_owned(),
                source,
            })
        }
        Err(source) => Err(PluginInstallError::Io {
            operation: "inspect plugin package directory",
            path: path.to_owned(),
            source,
        }),
    }
}

struct StagingGuard {
    path: PathBuf,
    present: bool,
}

impl StagingGuard {
    fn new(path: PathBuf) -> Self {
        Self {
            path,
            present: true,
        }
    }

    fn path(&self) -> &Path {
        &self.path
    }

    fn mark_moved(&mut self) {
        self.present = false;
    }

    fn mark_present(&mut self) {
        self.present = true;
    }
}

impl Drop for StagingGuard {
    fn drop(&mut self) {
        if self.present {
            let _ = fs::remove_dir_all(&self.path);
        }
    }
}

/// Result of installing or idempotently reopening one immutable plugin package.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginInstallResult {
    /// Stable plugin identifier.
    pub plugin_id: String,
    /// Exact plugin version.
    pub version: String,
    /// Absolute committed package path.
    pub package_path: PathBuf,
    /// Complete package byte length.
    pub package_size_bytes: u64,
    /// Complete package SHA-256.
    pub package_sha256: String,
    /// Whether a declared Component passed static validation.
    pub component_validated: bool,
    /// Whether this invocation committed a new package and state row.
    pub installed: bool,
    /// Permission change compared with the latest installed version.
    pub permissions: PluginPermissionsDiff,
}

/// Persisted state for one installed immutable plugin version.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginPackageStatus {
    /// Stable plugin identifier.
    pub plugin_id: String,
    /// Exact semantic version.
    pub version: String,
    /// Human-readable name from the validated manifest.
    pub name: String,
    /// Validated persisted manifest.
    pub manifest: PluginManifest,
    /// Absolute immutable package path.
    pub package_path: PathBuf,
    /// Complete package byte length.
    pub package_size_bytes: u64,
    /// Complete package SHA-256.
    pub package_sha256: String,
    /// Whether a declared Component passed static validation.
    pub component_validated: bool,
    /// Installation time in Unix seconds.
    pub installed_at_unix_seconds: u64,
    /// Whether this exact version is active.
    pub active: bool,
}

/// Result of enabling or disabling one plugin.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginActivationResult {
    /// Stable plugin identifier.
    pub plugin_id: String,
    /// Newly active version, or `None` after disabling.
    pub active_version: Option<String>,
    /// Whether persistent state changed.
    pub changed: bool,
}

/// Recoverable trash state for one plugin package.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginTrashStatus {
    /// Unique trash entry identifier.
    pub trash_id: String,
    /// Stable plugin identifier.
    pub plugin_id: String,
    /// Exact semantic version.
    pub version: String,
    /// Human-readable name from the validated manifest.
    pub name: String,
    /// Validated persisted manifest.
    pub manifest: PluginManifest,
    /// Absolute package path under workspace trash.
    pub package_path: PathBuf,
    /// Complete package byte length.
    pub package_size_bytes: u64,
    /// Complete package SHA-256.
    pub package_sha256: String,
    /// Whether a declared Component passed static validation.
    pub component_validated: bool,
    /// Original installation time in Unix seconds.
    pub installed_at_unix_seconds: u64,
    /// Time moved to trash in Unix seconds.
    pub trashed_at_unix_seconds: u64,
}

/// Result of moving a package to trash or restoring it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginTrashResult {
    /// Stable plugin identifier.
    pub plugin_id: String,
    /// Exact semantic version.
    pub version: String,
    /// Package path after the operation.
    pub package_path: PathBuf,
    /// Whether filesystem and persistent state changed.
    pub changed: bool,
}

/// Summary of interrupted plugin operations reconciled under the workspace lock.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginRecoveryResult {
    /// Renames that had completed and whose `SQLite` state was finalized.
    pub completed: usize,
    /// Journals cancelled because their rename had not occurred.
    pub cancelled: usize,
}

/// Plugin package installation failure.
#[derive(Debug, Error)]
pub enum PluginInstallError {
    /// A CLI or host request supplied an invalid plugin ID.
    #[error(transparent)]
    PluginId(#[from] PluginIdError),
    /// Workspace validation or locking failed.
    #[error(transparent)]
    Workspace(#[from] WorkspaceError),
    /// Package structure, manifest, or Component validation failed.
    #[error(transparent)]
    Package(#[from] PackageError),
    /// Host, API, or target compatibility failed.
    #[error(transparent)]
    Compatibility(#[from] CompatibilityError),
    /// A previously persisted manifest could not be parsed.
    #[error(transparent)]
    Manifest(#[from] softpilot_plugin_api::ManifestError),
    /// Manifest serialization failed before state persistence.
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    /// State database initialization or update failed.
    #[error(transparent)]
    Storage(#[from] StorageError),
    /// No installed package exists for the requested plugin.
    #[error("plugin is not installed: {0}")]
    PluginNotInstalled(String),
    /// The requested plugin version is not installed.
    #[error("plugin {plugin_id} version {version} is not installed")]
    PluginVersionNotInstalled {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact requested version.
        version: String,
    },
    /// The active plugin version must be disabled before uninstalling it.
    #[error("plugin {plugin_id} version {version} is active; disable it before uninstalling")]
    ActivePluginCannotUninstall {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact active version.
        version: String,
    },
    /// No recoverable trash entry exists for the requested version.
    #[error("plugin trash does not contain {plugin_id} version {version}")]
    TrashedPluginVersionNotFound {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact requested version.
        version: String,
    },
    /// Persisted package identity or path disagrees with its validated manifest.
    #[error("stored plugin package metadata does not match {plugin_id} version {version}")]
    StoredPackageMetadataMismatch {
        /// Plugin ID stored in the state row.
        plugin_id: String,
        /// Version stored in the state row.
        version: String,
    },
    /// Persisted trash identity or paths disagree with the validated manifest.
    #[error("stored plugin trash metadata does not match {plugin_id} version {version}")]
    StoredTrashMetadataMismatch {
        /// Plugin ID stored in the trash row.
        plugin_id: String,
        /// Version stored in the trash row.
        version: String,
    },
    /// A durable recovery journal contains paths or identity outside its operation contract.
    #[error("plugin recovery journal is invalid: {0}")]
    InvalidRecoveryJournal(String),
    /// Recovery cannot choose safely because both or neither side of a rename exists.
    #[error(
        "plugin recovery state is ambiguous for {operation_id}: source '{}' exists={source_exists}, destination '{}' exists={destination_exists}",
        source_path.display(),
        destination_path.display()
    )]
    AmbiguousRecoveryState {
        /// Durable operation identifier.
        operation_id: String,
        /// Expected pre-rename directory.
        source_path: PathBuf,
        /// Expected post-rename directory.
        destination_path: PathBuf,
        /// Whether the source directory exists.
        source_exists: bool,
        /// Whether the destination directory exists.
        destination_exists: bool,
    },
    /// A filesystem step failed.
    #[error("failed to {operation} at '{}': {source}", path.display())]
    Io {
        /// Human-readable installation stage.
        operation: &'static str,
        /// Path involved in the failure.
        path: PathBuf,
        /// Operating-system error.
        #[source]
        source: io::Error,
    },
    /// Requested permissions expand authority and require explicit approval.
    #[error("plugin installation requires confirmation for added permissions: {difference:?}")]
    PermissionConfirmationRequired {
        /// Stable permission additions and removals.
        difference: PluginPermissionsDiff,
    },
    /// A package ID/version is immutable once recorded.
    #[error(
        "plugin {plugin_id} version {version} already has SHA-256 {existing_sha256}; \
         requested {requested_sha256}"
    )]
    VersionDigestConflict {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact version being reused.
        version: String,
        /// Recorded package digest.
        existing_sha256: String,
        /// Requested package digest.
        requested_sha256: String,
    },
    /// A final version directory exists without corresponding state.
    #[error("plugin version directory exists without state: '{}'", .0.display())]
    UntrackedVersionDirectory(PathBuf),
    /// A path expected to be an owned real directory is a link or another file type.
    #[error("plugin directory is not a real directory: '{}'", .0.display())]
    UnsafeDirectory(PathBuf),
    /// Source bytes changed before or during the staging copy.
    #[error(
        "staged plugin digest differs from source: source {source_sha256}, staged {staged_sha256}"
    )]
    StagedPackageChanged {
        /// Digest inspected before taking the workspace lock.
        source_sha256: String,
        /// Digest inspected after staging.
        staged_sha256: String,
    },
    /// Reinspection produced different validated metadata despite matching bytes.
    #[error("staged plugin validation result differs from the source inspection")]
    StagedValidationChanged,
    /// An existing state row points to a package that no longer validates.
    #[error("committed plugin package is invalid at '{}': {source}", path.display())]
    CommittedPackageInvalid {
        /// Expected immutable package path.
        path: PathBuf,
        /// Package validation failure.
        source: PackageError,
    },
    /// A committed immutable package no longer matches its recorded state.
    #[error("committed plugin package changed after installation: '{}'", .0.display())]
    CommittedPackageChanged(PathBuf),
    /// State commit failed and the newly committed directory could not be moved back to staging.
    #[error(
        "plugin state commit failed ({storage}) and filesystem rollback failed at '{}': {rollback}",
        final_path.display()
    )]
    StateRollbackFailed {
        /// Newly committed final directory.
        final_path: PathBuf,
        /// Original state database failure.
        storage: Box<StorageError>,
        /// Filesystem rollback failure.
        rollback: Box<io::Error>,
    },
    /// A trash state update failed and the directory move could not be rolled back.
    #[error(
        "plugin trash state update failed ({storage}) and filesystem rollback failed at '{}': {rollback}",
        path.display()
    )]
    TrashStateRollbackFailed {
        /// Directory that could not be rolled back.
        path: PathBuf,
        /// Original state database failure.
        storage: Box<StorageError>,
        /// Filesystem rollback failure.
        rollback: Box<io::Error>,
    },
    /// System time cannot produce a valid installation timestamp.
    #[error("system clock is earlier than the Unix epoch")]
    ClockBeforeUnixEpoch,
}

#[cfg(test)]
mod tests {
    use super::*;
    use softpilot_core::HostTriple;
    use std::{env, io::Write};

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new(label: &str) -> Self {
            let path = env::temp_dir().join(format!(
                "softpilot-plugin-install-test-{label}-{}",
                Uuid::new_v4()
            ));
            fs::create_dir(&path).expect("create plugin install test directory");
            Self(path)
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            if self.0.starts_with(env::temp_dir())
                && self.0.file_name().is_some_and(|name| {
                    name.to_string_lossy()
                        .starts_with("softpilot-plugin-install-test-")
                })
            {
                let _ = fs::remove_dir_all(&self.0);
            }
        }
    }

    fn workspace_service(root: &Path) -> WorkspaceService {
        WorkspaceService::with_locations(
            root.join("bin/softpilot-workspace.json"),
            Some(root.join("config/bootstrap.json")),
            None,
        )
    }

    fn prepare_install_operation(
        workspace: &WorkspaceInfo,
        source_package: &Path,
    ) -> (PendingPluginFileOperation, PathBuf, PathBuf) {
        let inspected = PluginPackageInspector::inspect(source_package).expect("inspect fixture");
        let destination = InstallDestination::new(workspace, &inspected.manifest);
        fs::create_dir_all(&destination.package_id_directory).expect("create plugin ID directory");
        let operation_id = Uuid::new_v4().to_string();
        let source_relative_directory = format!("plugins/staging/install-{operation_id}");
        let staging_directory = workspace.path.as_path().join(&source_relative_directory);
        fs::create_dir(&staging_directory).expect("create interrupted staging directory");
        fs::copy(source_package, staging_directory.join(PACKAGE_FILE_NAME))
            .expect("copy interrupted package");
        let timestamp = unix_timestamp().expect("test timestamp");
        let operation = PendingPluginFileOperation {
            operation_id,
            kind: PluginFileOperationKind::Install,
            package: InstalledPluginPackage {
                plugin_id: destination.plugin_id.clone(),
                version: destination.version.clone(),
                package_sha256: inspected.package_sha256,
                package_size_bytes: inspected.package_size_bytes,
                relative_path: destination.relative_path,
                manifest_json: serde_json::to_string(&inspected.manifest)
                    .expect("serialize fixture manifest"),
                component_validated: inspected.component_validated,
                installed_at_unix_seconds: timestamp,
            },
            source_relative_directory,
            destination_relative_directory: destination.relative_directory.clone(),
            trash_id: None,
            trash_relative_path: None,
            operation_at_unix_seconds: timestamp,
        };
        (operation, staging_directory, destination.final_directory)
    }

    #[test]
    fn installs_reopens_and_rejects_reused_version_bytes() {
        let test = TestDirectory::new("idempotent");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let first_package = test.0.join("first.softpilot-plugin");
        write_plugin_package(
            &first_package,
            "dev.softpilot.install",
            "1.0.0",
            false,
            b"one",
        );

        let plugins = PluginService::new(workspaces);
        let installed = plugins
            .install(&workspace, &first_package, false)
            .expect("install package");
        assert!(installed.installed);
        assert!(installed.package_path.is_file());
        assert!(installed.permissions.is_unchanged());

        let repeated = plugins
            .install(&workspace, &first_package, false)
            .expect("repeat package");
        assert!(!repeated.installed);
        assert_eq!(repeated.package_sha256, installed.package_sha256);

        let changed_package = test.0.join("changed.softpilot-plugin");
        write_plugin_package(
            &changed_package,
            "dev.softpilot.install",
            "1.0.0",
            false,
            b"two",
        );
        assert!(matches!(
            plugins.install(&workspace, changed_package, false),
            Err(PluginInstallError::VersionDigestConflict { .. })
        ));
        assert_eq!(
            PluginPackageInspector::inspect(&installed.package_path)
                .expect("reinspect committed package")
                .package_sha256,
            installed.package_sha256
        );
    }

    #[test]
    fn requires_permission_confirmation_before_creating_staging() {
        let test = TestDirectory::new("permissions");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package = test.0.join("permissions.softpilot-plugin");
        write_plugin_package(
            &package,
            "dev.softpilot.permissions",
            "1.0.0",
            true,
            b"permissions",
        );
        let plugins = PluginService::new(workspaces);

        let error = plugins
            .install(&workspace, &package, false)
            .expect_err("permission expansion requires confirmation");
        assert!(matches!(
            error,
            PluginInstallError::PermissionConfirmationRequired { .. }
        ));
        let staging = workspace.path.as_path().join("plugins/staging");
        assert_eq!(
            fs::read_dir(&staging).expect("read empty staging").count(),
            0
        );
        assert!(
            !workspace
                .path
                .as_path()
                .join("plugins/packages")
                .join(workspace.host_triple.as_str())
                .join("dev.softpilot.permissions")
                .exists()
        );

        let installed = plugins
            .install(&workspace, package, true)
            .expect("install with confirmed permission");
        assert!(installed.installed);
        assert_eq!(installed.permissions.added.len(), 1);
    }

    #[test]
    fn rolls_back_committed_directory_when_state_insert_fails() {
        let test = TestDirectory::new("state-rollback");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package = test.0.join("rollback.softpilot-plugin");
        write_plugin_package(
            &package,
            "dev.softpilot.rollback",
            "1.0.0",
            false,
            b"rollback",
        );
        let mut database = open_state_database(&workspace).expect("open state database");
        database
            .transaction(|transaction| {
                transaction.execute_batch(
                    "CREATE TRIGGER reject_plugin_insert \
                     BEFORE INSERT ON plugin_packages \
                     BEGIN SELECT RAISE(ABORT, 'injected state failure'); END;",
                )
            })
            .expect("install failure trigger");
        drop(database);

        let error = PluginService::new(workspaces)
            .install(&workspace, package, false)
            .expect_err("state insert must fail");
        assert!(matches!(error, PluginInstallError::Storage(_)));
        assert!(
            !workspace
                .path
                .as_path()
                .join("plugins/packages")
                .join(workspace.host_triple.as_str())
                .join("dev.softpilot.rollback/1.0.0")
                .exists()
        );
        assert_eq!(
            fs::read_dir(workspace.path.as_path().join("plugins/staging"))
                .expect("read staging after rollback")
                .count(),
            0
        );
    }

    #[test]
    fn recovery_cancels_an_install_interrupted_before_rename() {
        let test = TestDirectory::new("recover-install-before-rename");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package = test.0.join("recover-before.softpilot-plugin");
        write_plugin_package(
            &package,
            "dev.softpilot.recover-before",
            "1.0.0",
            false,
            b"before",
        );
        let (operation, staging, final_directory) = prepare_install_operation(&workspace, &package);
        let mut database = open_state_database(&workspace).expect("open state database");
        database
            .begin_plugin_file_operation(&operation)
            .expect("journal interrupted install");
        drop(database);

        let recovered = PluginService::new(workspaces)
            .recover(&workspace)
            .expect("recover pre-rename install");
        assert_eq!(recovered.cancelled, 1);
        assert_eq!(recovered.completed, 0);
        assert!(!staging.exists());
        assert!(!final_directory.exists());
        let database = open_state_database(&workspace).expect("reopen state database");
        assert!(
            database
                .pending_plugin_file_operations()
                .expect("read cleared journal")
                .is_empty()
        );
    }

    #[test]
    fn recovery_completes_an_install_interrupted_after_rename() {
        let test = TestDirectory::new("recover-install-after-rename");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package = test.0.join("recover-after.softpilot-plugin");
        write_plugin_package(
            &package,
            "dev.softpilot.recover-after",
            "1.0.0",
            false,
            b"after",
        );
        let (operation, staging, final_directory) = prepare_install_operation(&workspace, &package);
        let mut database = open_state_database(&workspace).expect("open state database");
        database
            .begin_plugin_file_operation(&operation)
            .expect("journal interrupted install");
        drop(database);
        fs::rename(&staging, &final_directory).expect("simulate completed directory rename");

        let plugins = PluginService::new(workspaces);
        let recovered = plugins
            .recover(&workspace)
            .expect("recover post-rename install");
        assert_eq!(recovered.completed, 1);
        assert_eq!(recovered.cancelled, 0);
        assert!(final_directory.join(PACKAGE_FILE_NAME).is_file());
        let installed = plugins.list(&workspace).expect("list recovered install");
        assert_eq!(installed.len(), 1);
        assert_eq!(installed[0].plugin_id, "dev.softpilot.recover-after");
    }

    #[test]
    fn recovery_rejects_ambiguous_install_paths_without_changing_state() {
        let test = TestDirectory::new("recover-install-ambiguous");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package = test.0.join("recover-ambiguous.softpilot-plugin");
        write_plugin_package(
            &package,
            "dev.softpilot.recover-ambiguous",
            "1.0.0",
            false,
            b"ambiguous",
        );
        let (operation, staging, final_directory) = prepare_install_operation(&workspace, &package);
        fs::create_dir(&final_directory).expect("create conflicting final directory");
        fs::copy(
            staging.join(PACKAGE_FILE_NAME),
            final_directory.join(PACKAGE_FILE_NAME),
        )
        .expect("copy conflicting package");
        let mut database = open_state_database(&workspace).expect("open state database");
        database
            .begin_plugin_file_operation(&operation)
            .expect("journal ambiguous install");
        drop(database);

        assert!(matches!(
            PluginService::new(workspaces).recover(&workspace),
            Err(PluginInstallError::AmbiguousRecoveryState { .. })
        ));
        assert!(staging.is_dir());
        assert!(final_directory.is_dir());
        let database = open_state_database(&workspace).expect("reopen state database");
        assert_eq!(
            database
                .pending_plugin_file_operations()
                .expect("journal remains for diagnosis"),
            vec![operation]
        );
    }

    #[test]
    fn lists_enables_switches_and_disables_installed_versions() {
        let test = TestDirectory::new("activation");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let first = test.0.join("first.softpilot-plugin");
        let second = test.0.join("second.softpilot-plugin");
        write_plugin_package(&first, "dev.softpilot.active", "1.0.0", false, b"one");
        write_plugin_package(&second, "dev.softpilot.active", "2.0.0", false, b"two");
        let plugins = PluginService::new(workspaces);
        plugins
            .install(&workspace, first, false)
            .expect("install first version");
        let second_installed = plugins
            .install(&workspace, second, false)
            .expect("install second version");

        let installed = plugins.list(&workspace).expect("list installed versions");
        assert_eq!(installed.len(), 2);
        assert!(installed.iter().all(|status| !status.active));

        let latest = plugins
            .enable(&workspace, "dev.softpilot.active", None)
            .expect("enable latest version");
        assert_eq!(latest.active_version.as_deref(), Some("2.0.0"));
        assert!(latest.changed);
        assert!(
            !plugins
                .enable(&workspace, "dev.softpilot.active", None)
                .expect("repeat latest activation")
                .changed
        );

        let switched = plugins
            .enable(&workspace, "dev.softpilot.active", Some("1.0.0"))
            .expect("switch active version");
        assert_eq!(switched.active_version.as_deref(), Some("1.0.0"));
        let listed = plugins.list(&workspace).expect("list switched versions");
        assert!(
            listed
                .iter()
                .any(|status| status.version == "1.0.0" && status.active)
        );
        assert!(
            listed
                .iter()
                .any(|status| status.version == "2.0.0" && !status.active)
        );

        assert!(
            plugins
                .disable(&workspace, "dev.softpilot.active")
                .expect("disable plugin")
                .changed
        );
        assert!(
            !plugins
                .disable(&workspace, "dev.softpilot.active")
                .expect("repeat disable")
                .changed
        );
        assert!(matches!(
            plugins.enable(&workspace, "dev.softpilot.active", Some("3.0.0")),
            Err(PluginInstallError::PluginVersionNotInstalled { .. })
        ));

        fs::write(&second_installed.package_path, b"tampered")
            .expect("tamper committed package fixture");
        assert!(matches!(
            plugins.enable(&workspace, "dev.softpilot.active", Some("2.0.0")),
            Err(PluginInstallError::CommittedPackageInvalid { .. }
                | PluginInstallError::CommittedPackageChanged(_))
        ));
        assert!(
            plugins
                .list(&workspace)
                .expect("list after rejected activation")
                .iter()
                .all(|status| !status.active)
        );
    }

    #[test]
    fn uninstalls_to_trash_and_restores_without_touching_plugin_data() {
        let test = TestDirectory::new("trash");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package = test.0.join("trash.softpilot-plugin");
        write_plugin_package(&package, "dev.softpilot.trash", "1.0.0", false, b"trash");
        let plugins = PluginService::new(workspaces);
        let installed = plugins
            .install(&workspace, package, false)
            .expect("install trash fixture");
        let plugin_data = workspace
            .path
            .as_path()
            .join("plugins/data/dev.softpilot.trash");
        fs::create_dir(&plugin_data).expect("create plugin data directory");
        fs::write(plugin_data.join("preserve.txt"), b"preserve")
            .expect("write plugin data sentinel");
        plugins
            .enable(&workspace, "dev.softpilot.trash", Some("1.0.0"))
            .expect("enable trash fixture");
        assert!(matches!(
            plugins.uninstall(&workspace, "dev.softpilot.trash", "1.0.0"),
            Err(PluginInstallError::ActivePluginCannotUninstall { .. })
        ));
        assert!(installed.package_path.is_file());

        plugins
            .disable(&workspace, "dev.softpilot.trash")
            .expect("disable trash fixture");
        let trashed = plugins
            .uninstall(&workspace, "dev.softpilot.trash", "1.0.0")
            .expect("uninstall to trash");
        assert!(trashed.changed);
        assert!(trashed.package_path.is_file());
        assert!(!installed.package_path.exists());
        assert!(
            plugins
                .list(&workspace)
                .expect("list after trash")
                .is_empty()
        );
        assert_eq!(plugins.trash(&workspace).expect("list trash").len(), 1);
        assert!(
            !plugins
                .uninstall(&workspace, "dev.softpilot.trash", "1.0.0")
                .expect("repeat uninstall")
                .changed
        );

        let restored = plugins
            .restore(&workspace, "dev.softpilot.trash", "1.0.0")
            .expect("restore trash fixture");
        assert!(restored.changed);
        assert_eq!(restored.package_path, installed.package_path);
        assert!(restored.package_path.is_file());
        assert!(plugins.trash(&workspace).expect("empty trash").is_empty());
        assert!(
            !plugins
                .restore(&workspace, "dev.softpilot.trash", "1.0.0")
                .expect("repeat restore")
                .changed
        );
        assert_eq!(
            fs::read(plugin_data.join("preserve.txt")).expect("read plugin data sentinel"),
            b"preserve"
        );
    }

    #[test]
    fn recovery_completes_interrupted_trash_and_restore_renames() {
        let test = TestDirectory::new("recover-trash-restore");
        let workspaces = workspace_service(&test.0);
        let workspace = workspaces
            .initialize(&test.0.join("workspace"))
            .expect("initialize workspace")
            .workspace;
        let package_path = test.0.join("recover-trash.softpilot-plugin");
        write_plugin_package(
            &package_path,
            "dev.softpilot.recover-trash",
            "1.0.0",
            false,
            b"trash-restore",
        );
        let plugins = PluginService::new(workspaces);
        let installed = plugins
            .install(&workspace, package_path, false)
            .expect("install recovery fixture");
        let mut database = open_state_database(&workspace).expect("open state database");
        let package = database
            .plugin_package("dev.softpilot.recover-trash", "1.0.0")
            .expect("read installed package")
            .expect("installed package state");
        let trash_id = Uuid::new_v4().to_string();
        let trash_directory = workspace
            .path
            .as_path()
            .join("plugins/trash")
            .join(&trash_id);
        let trash_relative_path = format!("plugins/trash/{trash_id}/{PACKAGE_FILE_NAME}");
        let trashed_at = unix_timestamp().expect("trash timestamp");
        let trash_operation = PendingPluginFileOperation {
            operation_id: Uuid::new_v4().to_string(),
            kind: PluginFileOperationKind::Trash,
            package: package.clone(),
            source_relative_directory: format!(
                "plugins/packages/{}/dev.softpilot.recover-trash/1.0.0",
                workspace.host_triple.as_str()
            ),
            destination_relative_directory: format!("plugins/trash/{trash_id}"),
            trash_id: Some(trash_id.clone()),
            trash_relative_path: Some(trash_relative_path.clone()),
            operation_at_unix_seconds: trashed_at,
        };
        database
            .begin_plugin_file_operation(&trash_operation)
            .expect("journal interrupted trash");
        drop(database);
        let installed_directory = installed
            .package_path
            .parent()
            .expect("installed version directory");
        fs::rename(installed_directory, &trash_directory)
            .expect("simulate interrupted trash rename");

        assert_eq!(
            plugins
                .recover(&workspace)
                .expect("recover trash operation")
                .completed,
            1
        );
        assert!(plugins.list(&workspace).expect("installed list").is_empty());
        assert_eq!(plugins.trash(&workspace).expect("trash list").len(), 1);

        let mut database = open_state_database(&workspace).expect("reopen state database");
        let restore_operation = PendingPluginFileOperation {
            operation_id: Uuid::new_v4().to_string(),
            kind: PluginFileOperationKind::Restore,
            package,
            source_relative_directory: format!("plugins/trash/{trash_id}"),
            destination_relative_directory: format!(
                "plugins/packages/{}/dev.softpilot.recover-trash/1.0.0",
                workspace.host_triple.as_str()
            ),
            trash_id: Some(trash_id),
            trash_relative_path: Some(trash_relative_path),
            operation_at_unix_seconds: trashed_at,
        };
        database
            .begin_plugin_file_operation(&restore_operation)
            .expect("journal interrupted restore");
        drop(database);
        fs::rename(&trash_directory, installed_directory)
            .expect("simulate interrupted restore rename");

        assert_eq!(
            plugins
                .recover(&workspace)
                .expect("recover restore operation")
                .completed,
            1
        );
        assert_eq!(plugins.list(&workspace).expect("restored list").len(), 1);
        assert!(plugins.trash(&workspace).expect("empty trash").is_empty());
    }

    fn write_plugin_package(
        path: &Path,
        plugin_id: &str,
        version: &str,
        requests_process: bool,
        payload: &[u8],
    ) {
        let target = HostTriple::detect()
            .expect("supported test host")
            .platform_target();
        let process = if requests_process {
            serde_json::json!(["staged"])
        } else {
            serde_json::json!([])
        };
        let manifest = serde_json::json!({
            "schemaVersion": "0.1.0",
            "id": plugin_id,
            "version": version,
            "pluginApi": "0.1.0",
            "hostVersion": ">=0.1.0, <0.2.0",
            "name": "Install fixture",
            "description": "Plugin installation fixture",
            "publisher": { "name": "SoftPilot" },
            "license": "MIT",
            "kind": "application",
            "managementLevel": "workspace",
            "entry": { "type": "recipe", "recipe": "recipe.json" },
            "targets": [target],
            "permissions": {
                "network": { "catalogOrigins": [], "artifactOrigins": [] },
                "process": process,
                "shell": [],
                "os": []
            }
        });
        let manifest = serde_json::to_vec(&manifest).expect("serialize install manifest");
        write_stored_zip(
            path,
            &[
                ("plugin.json", manifest.as_slice()),
                ("recipe.json", b"{}"),
                ("payload.bin", payload),
            ],
        );
    }

    fn write_stored_zip(path: &Path, entries: &[(&str, &[u8])]) {
        struct CentralEntry {
            name: Vec<u8>,
            crc32: u32,
            size: u32,
            offset: u32,
        }

        let mut file = File::create(path).expect("create test plugin package");
        let mut central = Vec::new();
        let mut offset = 0_u32;
        for (name, contents) in entries {
            let name = name.as_bytes();
            let size = u32::try_from(contents.len()).expect("small fixture entry");
            let name_length = u16::try_from(name.len()).expect("short fixture path");
            let crc32 = crc32(contents);

            write_u32(&mut file, 0x0403_4b50);
            write_u16(&mut file, 20);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u32(&mut file, crc32);
            write_u32(&mut file, size);
            write_u32(&mut file, size);
            write_u16(&mut file, name_length);
            write_u16(&mut file, 0);
            file.write_all(name).expect("write local entry name");
            file.write_all(contents).expect("write local entry");

            central.push(CentralEntry {
                name: name.to_vec(),
                crc32,
                size,
                offset,
            });
            offset = offset
                .checked_add(30)
                .and_then(|value| value.checked_add(u32::from(name_length)))
                .and_then(|value| value.checked_add(size))
                .expect("small fixture archive");
        }

        let central_offset = offset;
        for entry in &central {
            write_u32(&mut file, 0x0201_4b50);
            write_u16(&mut file, 20);
            write_u16(&mut file, 20);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u32(&mut file, entry.crc32);
            write_u32(&mut file, entry.size);
            write_u32(&mut file, entry.size);
            write_u16(
                &mut file,
                u16::try_from(entry.name.len()).expect("short central path"),
            );
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u16(&mut file, 0);
            write_u32(&mut file, 0);
            write_u32(&mut file, entry.offset);
            file.write_all(&entry.name).expect("write central entry");
            offset = offset
                .checked_add(46)
                .and_then(|value| {
                    value.checked_add(u32::try_from(entry.name.len()).expect("short central path"))
                })
                .expect("small fixture archive");
        }
        let central_size = offset
            .checked_sub(central_offset)
            .expect("central directory follows local entries");
        let entry_count = u16::try_from(central.len()).expect("few fixture entries");
        write_u32(&mut file, 0x0605_4b50);
        write_u16(&mut file, 0);
        write_u16(&mut file, 0);
        write_u16(&mut file, entry_count);
        write_u16(&mut file, entry_count);
        write_u32(&mut file, central_size);
        write_u32(&mut file, central_offset);
        write_u16(&mut file, 0);
        file.sync_all().expect("flush test plugin package");
    }

    fn crc32(bytes: &[u8]) -> u32 {
        let mut value = u32::MAX;
        for byte in bytes {
            value ^= u32::from(*byte);
            for _ in 0..8 {
                value = (value >> 1) ^ (0xedb8_8320 & (0_u32.wrapping_sub(value & 1)));
            }
        }
        !value
    }

    fn write_u16(writer: &mut impl Write, value: u16) {
        writer
            .write_all(&value.to_le_bytes())
            .expect("write ZIP u16");
    }

    fn write_u32(writer: &mut impl Write, value: u32) {
        writer
            .write_all(&value.to_le_bytes())
            .expect("write ZIP u32");
    }
}
