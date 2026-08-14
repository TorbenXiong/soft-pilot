//! Shared host use cases that coordinate workspace state and platform-facing file operations.

mod plugin;

pub use plugin::{
    PluginActivationResult, PluginInstallError, PluginInstallResult, PluginPackageStatus,
    PluginRecoveryResult, PluginService, PluginTrashResult, PluginTrashStatus,
};

use std::{
    env, fs,
    fs::{File, OpenOptions, TryLockError},
    io::{self, Write},
    path::{Path, PathBuf},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use softpilot_core::{
    HostTriple, HostTripleError, WorkspaceId, WorkspaceLayoutVersionError, WorkspaceMetadata,
    WorkspacePath, WorkspacePathError,
};
use softpilot_storage::{StateDatabase, StateDatabaseIdentity, StorageError};
use thiserror::Error;
use uuid::Uuid;

const METADATA_FILE_NAME: &str = "workspace.json";
const LOCK_FILE_NAME: &str = "workspace.lock";
const LOCK_HOLDER_FILE_NAME: &str = "workspace.lock.owner.json";
const PORTABLE_POINTER_FILE_NAME: &str = "softpilot-workspace.json";
const POINTER_FORMAT_VERSION: u32 = 1;
const LOCK_HOLDER_FORMAT_VERSION: u32 = 1;
const LOCK_RETRY_INTERVAL: Duration = Duration::from_millis(25);

/// Origin used to locate a workspace for the current invocation.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum WorkspaceSource {
    /// An explicit CLI or host argument.
    Explicit,
    /// The `SOFTPILOT_WORKSPACE` environment variable.
    Environment,
    /// A locator file beside the current executable.
    PortablePointer,
    /// The current user's `SoftPilot` bootstrap file.
    UserBootstrap,
}

impl WorkspaceSource {
    /// Returns the stable human-readable source name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Explicit => "explicit",
            Self::Environment => "environment",
            Self::PortablePointer => "portable-pointer",
            Self::UserBootstrap => "user-bootstrap",
        }
    }
}

/// A validated workspace location before its metadata is opened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkspaceLocation {
    /// Validated absolute workspace root.
    pub path: WorkspacePath,
    /// Source that selected this root.
    pub source: WorkspaceSource,
}

/// Read model returned to CLI and GUI callers.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkspaceInfo {
    /// Validated absolute workspace root.
    pub path: WorkspacePath,
    /// Persistent workspace identity and compatibility metadata.
    pub metadata: WorkspaceMetadata,
    /// Host-specific directory selected for this process.
    pub host_triple: HostTriple,
    /// Source that located the workspace.
    pub source: WorkspaceSource,
}

/// Result of an idempotent workspace initialization request.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkspaceInitResult {
    /// Workspace read model after initialization.
    pub workspace: WorkspaceInfo,
    /// Whether this invocation created new workspace metadata and directories.
    pub created: bool,
}

/// Diagnostic metadata written to the workspace lock file while an operation owns the lock.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WorkspaceLockHolder {
    /// Diagnostic record format version.
    pub format_version: u32,
    /// Unique identity for this lock acquisition.
    pub owner_id: WorkspaceId,
    /// Operating system process identifier.
    pub process_id: u32,
    /// Validated operation name supplied by the caller.
    pub operation: String,
    /// Acquisition time measured in whole seconds since the Unix epoch.
    pub acquired_at_unix_seconds: u64,
}

/// Exclusive workspace lock released automatically when dropped.
#[derive(Debug)]
pub struct WorkspaceLockGuard {
    file: File,
    path: PathBuf,
    holder_path: PathBuf,
    holder: WorkspaceLockHolder,
}

impl WorkspaceLockGuard {
    /// Returns the diagnostic record associated with this acquisition.
    #[must_use]
    pub const fn holder(&self) -> &WorkspaceLockHolder {
        &self.holder
    }

    /// Returns the workspace lock-file path.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for WorkspaceLockGuard {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.holder_path);
        let _ = File::unlock(&self.file);
    }
}

/// Shared workspace initialization, lookup, and metadata service.
#[derive(Debug, Clone)]
pub struct WorkspaceService {
    portable_pointer: PathBuf,
    bootstrap_pointer: Option<PathBuf>,
    environment_workspace: Option<PathBuf>,
}

impl WorkspaceService {
    /// Creates a service using the current executable and user environment.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError`] when the current executable path cannot be determined.
    pub fn for_current_process() -> Result<Self, WorkspaceError> {
        let executable = env::current_exe().map_err(|source| WorkspaceError::Io {
            operation: "locate the current executable",
            path: PathBuf::from("<current executable>"),
            source,
        })?;
        let executable_directory = executable
            .parent()
            .ok_or_else(|| WorkspaceError::ExecutableHasNoParent(executable.clone()))?;

        Ok(Self {
            portable_pointer: executable_directory.join(PORTABLE_POINTER_FILE_NAME),
            bootstrap_pointer: current_user_bootstrap_file(),
            environment_workspace: env::var_os("SOFTPILOT_WORKSPACE").map(PathBuf::from),
        })
    }

    /// Creates a service with explicit locations, primarily for embedding and deterministic tests.
    #[must_use]
    pub fn with_locations(
        portable_pointer: PathBuf,
        bootstrap_pointer: Option<PathBuf>,
        environment_workspace: Option<PathBuf>,
    ) -> Self {
        Self {
            portable_pointer,
            bootstrap_pointer,
            environment_workspace,
        }
    }

    /// Resolves the first configured workspace using the documented precedence order.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError`] when a configured higher-precedence source is invalid. Invalid
    /// configuration is not silently ignored in favor of a lower-precedence workspace.
    pub fn locate(
        &self,
        explicit: Option<&Path>,
    ) -> Result<Option<WorkspaceLocation>, WorkspaceError> {
        if let Some(path) = explicit {
            return Ok(Some(WorkspaceLocation {
                path: resolve_existing_workspace_path(path)?,
                source: WorkspaceSource::Explicit,
            }));
        }

        if let Some(path) = &self.environment_workspace {
            return Ok(Some(WorkspaceLocation {
                path: resolve_existing_workspace_path(path)?,
                source: WorkspaceSource::Environment,
            }));
        }

        if self
            .portable_pointer
            .try_exists()
            .map_err(|source| WorkspaceError::Io {
                operation: "inspect the portable workspace pointer",
                path: self.portable_pointer.clone(),
                source,
            })?
        {
            return Ok(Some(WorkspaceLocation {
                path: read_pointer(&self.portable_pointer, true)?,
                source: WorkspaceSource::PortablePointer,
            }));
        }

        if let Some(pointer) = &self.bootstrap_pointer
            && pointer.try_exists().map_err(|source| WorkspaceError::Io {
                operation: "inspect the user workspace bootstrap",
                path: pointer.clone(),
                source,
            })?
        {
            return Ok(Some(WorkspaceLocation {
                path: read_pointer(pointer, false)?,
                source: WorkspaceSource::UserBootstrap,
            }));
        }

        Ok(None)
    }

    /// Locates and opens a workspace without mutating its contents.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError`] for invalid pointers, missing metadata, unsupported layouts, or
    /// an incomplete shared directory layout.
    pub fn resolve(
        &self,
        explicit: Option<&Path>,
    ) -> Result<Option<WorkspaceInfo>, WorkspaceError> {
        self.locate(explicit)?
            .map(|location| self.open(&location))
            .transpose()
    }

    /// Reads and validates workspace metadata without creating or repairing directories.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError`] when metadata or required shared directories are unavailable.
    pub fn open(&self, location: &WorkspaceLocation) -> Result<WorkspaceInfo, WorkspaceError> {
        let metadata_path = location.path.as_path().join(METADATA_FILE_NAME);
        if !metadata_path
            .try_exists()
            .map_err(|source| WorkspaceError::Io {
                operation: "inspect workspace metadata",
                path: metadata_path.clone(),
                source,
            })?
        {
            return Err(WorkspaceError::MetadataMissing(metadata_path));
        }

        let metadata: WorkspaceMetadata = read_json(&metadata_path, "read workspace metadata")?;
        let metadata = metadata.ensure_supported()?;
        validate_shared_layout(location.path.as_path())?;

        Ok(WorkspaceInfo {
            path: location.path.clone(),
            metadata,
            host_triple: HostTriple::detect()?,
            source: location.source,
        })
    }

    /// Initializes a new workspace or prepares the current host directory in an existing one.
    ///
    /// A new workspace is assembled in a sibling staging directory and renamed into place. An
    /// existing directory is accepted only when it is empty or already contains valid metadata.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError`] without overwriting unrecognized files or unsupported metadata.
    pub fn initialize(&self, requested: &Path) -> Result<WorkspaceInitResult, WorkspaceError> {
        let prepared = prepare_workspace_path(requested)?;
        let metadata_path = prepared.path.as_path().join(METADATA_FILE_NAME);

        if metadata_path
            .try_exists()
            .map_err(|source| WorkspaceError::Io {
                operation: "inspect existing workspace metadata",
                path: metadata_path.clone(),
                source,
            })?
        {
            let location = WorkspaceLocation {
                path: prepared.path,
                source: WorkspaceSource::Explicit,
            };
            let workspace = self.open(&location)?;
            let _lock = self.acquire_lock(
                &workspace.path,
                "workspace.initialize",
                Duration::from_secs(10),
            )?;
            ensure_host_layout(workspace.path.as_path(), workspace.host_triple)?;
            initialize_state_database(
                workspace.path.as_path(),
                workspace.host_triple,
                workspace.metadata,
            )?;
            return Ok(WorkspaceInitResult {
                workspace,
                created: false,
            });
        }

        if prepared.exists && directory_has_entries(prepared.path.as_path())? {
            return Err(WorkspaceError::DirectoryNotEmpty(
                prepared.path.into_path_buf(),
            ));
        }

        let host_triple = HostTriple::detect()?;
        let workspace_id = WorkspaceId::generate();
        let created_at_unix_seconds = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| WorkspaceError::ClockBeforeUnixEpoch)?
            .as_secs();
        let metadata = WorkspaceMetadata::new(workspace_id, created_at_unix_seconds);
        let staging = staging_path(prepared.path.as_path(), workspace_id)?;

        if let Err(error) = build_staged_workspace(&staging, host_triple, &metadata) {
            cleanup_owned_staging(&staging);
            return Err(error);
        }

        if prepared.exists
            && let Err(source) = fs::remove_dir(prepared.path.as_path())
        {
            cleanup_owned_staging(&staging);
            if source.kind() == io::ErrorKind::NotFound
                && let Some(workspace) = self.open_if_initialized(&prepared.path)?
            {
                return Ok(WorkspaceInitResult {
                    workspace,
                    created: false,
                });
            }
            return Err(WorkspaceError::Io {
                operation: "replace the selected empty workspace directory",
                path: prepared.path.as_path().to_owned(),
                source,
            });
        }

        if let Err(source) = fs::rename(&staging, prepared.path.as_path()) {
            cleanup_owned_staging(&staging);
            if let Some(workspace) = self.open_if_initialized(&prepared.path)? {
                return Ok(WorkspaceInitResult {
                    workspace,
                    created: false,
                });
            }
            if prepared.exists {
                let _ = fs::create_dir(prepared.path.as_path());
            }
            return Err(WorkspaceError::Io {
                operation: "commit the initialized workspace",
                path: prepared.path.as_path().to_owned(),
                source,
            });
        }

        let workspace = self.open(&WorkspaceLocation {
            path: prepared.path,
            source: WorkspaceSource::Explicit,
        })?;
        Ok(WorkspaceInitResult {
            workspace,
            created: true,
        })
    }

    fn open_if_initialized(
        &self,
        path: &WorkspacePath,
    ) -> Result<Option<WorkspaceInfo>, WorkspaceError> {
        if !path
            .as_path()
            .join(METADATA_FILE_NAME)
            .try_exists()
            .map_err(|source| WorkspaceError::Io {
                operation: "inspect a concurrently initialized workspace",
                path: path.as_path().to_owned(),
                source,
            })?
        {
            return Ok(None);
        }
        self.open(&WorkspaceLocation {
            path: path.clone(),
            source: WorkspaceSource::Explicit,
        })
        .map(Some)
    }

    /// Stores the selected workspace in the current user's bootstrap file.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError::UserConfigDirectoryUnavailable`] when no supported per-user
    /// configuration directory can be derived, or an I/O error if the pointer cannot be replaced.
    pub fn remember(&self, workspace: &WorkspacePath) -> Result<(), WorkspaceError> {
        let pointer = self
            .bootstrap_pointer
            .as_ref()
            .ok_or(WorkspaceError::UserConfigDirectoryUnavailable)?;
        let value = WorkspacePointer {
            format_version: POINTER_FORMAT_VERSION,
            workspace: workspace.as_path().to_owned(),
        };
        write_json_replacing(pointer, &value, "write user workspace bootstrap")
    }

    /// Acquires the exclusive cross-process lock for a workspace.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceError`] for invalid input, an invalid lock file, I/O failure, or a
    /// timeout. Timeout errors include the last readable holder diagnostic.
    pub fn acquire_lock(
        &self,
        workspace: &WorkspacePath,
        operation: &str,
        timeout: Duration,
    ) -> Result<WorkspaceLockGuard, WorkspaceError> {
        validate_lock_operation(operation)?;
        let path = workspace.as_path().join(LOCK_FILE_NAME);
        let holder_path = workspace.as_path().join(LOCK_HOLDER_FILE_NAME);
        validate_lock_file(&path)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
            .map_err(|source| WorkspaceError::Io {
                operation: "open the workspace lock",
                path: path.clone(),
                source,
            })?;
        let started = Instant::now();

        loop {
            match file.try_lock() {
                Ok(()) => {
                    let holder = WorkspaceLockHolder {
                        format_version: LOCK_HOLDER_FORMAT_VERSION,
                        owner_id: WorkspaceId::generate(),
                        process_id: std::process::id(),
                        operation: operation.to_owned(),
                        acquired_at_unix_seconds: SystemTime::now()
                            .duration_since(UNIX_EPOCH)
                            .map_err(|_| WorkspaceError::ClockBeforeUnixEpoch)?
                            .as_secs(),
                    };
                    if let Err(error) = write_lock_holder(&holder_path, &holder) {
                        let _ = File::unlock(&file);
                        return Err(error);
                    }
                    return Ok(WorkspaceLockGuard {
                        file,
                        path,
                        holder_path,
                        holder,
                    });
                }
                Err(TryLockError::WouldBlock) => {
                    if started.elapsed() >= timeout {
                        return Err(WorkspaceError::LockTimeout {
                            path: path.clone(),
                            timeout_milliseconds: timeout.as_millis(),
                            holder: read_lock_holder(&holder_path),
                        });
                    }
                    thread::sleep(
                        LOCK_RETRY_INTERVAL.min(timeout.saturating_sub(started.elapsed())),
                    );
                }
                Err(TryLockError::Error(source)) => {
                    return Err(WorkspaceError::Io {
                        operation: "acquire the workspace lock",
                        path,
                        source,
                    });
                }
            }
        }
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WorkspacePointer {
    format_version: u32,
    workspace: PathBuf,
}

struct PreparedWorkspacePath {
    path: WorkspacePath,
    exists: bool,
}

fn prepare_workspace_path(requested: &Path) -> Result<PreparedWorkspacePath, WorkspaceError> {
    let requested = WorkspacePath::new(requested)?;
    match fs::symlink_metadata(requested.as_path()) {
        Ok(_) => {
            if !requested.as_path().is_dir() {
                return Err(WorkspaceError::NotDirectory(requested.into_path_buf()));
            }
            let canonical = normalize_canonical_path(
                fs::canonicalize(requested.as_path()).map_err(|source| WorkspaceError::Io {
                    operation: "resolve the selected workspace directory",
                    path: requested.as_path().to_owned(),
                    source,
                })?,
            );
            Ok(PreparedWorkspacePath {
                path: WorkspacePath::new(&canonical)?,
                exists: true,
            })
        }
        Err(source) if source.kind() == io::ErrorKind::NotFound => {
            let parent = requested.as_path().parent().ok_or_else(|| {
                WorkspaceError::WorkspaceParentMissing(requested.as_path().to_owned())
            })?;
            let canonical_parent =
                normalize_canonical_path(fs::canonicalize(parent).map_err(|source| {
                    WorkspaceError::Io {
                        operation: "resolve the workspace parent directory",
                        path: parent.to_owned(),
                        source,
                    }
                })?);
            let name = requested.as_path().file_name().ok_or_else(|| {
                WorkspaceError::WorkspaceParentMissing(requested.as_path().to_owned())
            })?;
            Ok(PreparedWorkspacePath {
                path: WorkspacePath::new(&canonical_parent.join(name))?,
                exists: false,
            })
        }
        Err(source) => Err(WorkspaceError::Io {
            operation: "inspect the selected workspace path",
            path: requested.as_path().to_owned(),
            source,
        }),
    }
}

fn resolve_existing_workspace_path(path: &Path) -> Result<WorkspacePath, WorkspaceError> {
    let path = WorkspacePath::new(path)?;
    if !path.as_path().is_dir() {
        return Err(WorkspaceError::NotDirectory(path.into_path_buf()));
    }
    let canonical =
        normalize_canonical_path(fs::canonicalize(path.as_path()).map_err(|source| {
            WorkspaceError::Io {
                operation: "resolve the configured workspace directory",
                path: path.as_path().to_owned(),
                source,
            }
        })?);
    WorkspacePath::new(&canonical).map_err(Into::into)
}

fn read_pointer(path: &Path, relative_allowed: bool) -> Result<WorkspacePath, WorkspaceError> {
    let pointer: WorkspacePointer = read_json(path, "read workspace pointer")?;
    if pointer.format_version != POINTER_FORMAT_VERSION {
        return Err(WorkspaceError::UnsupportedPointerVersion {
            path: path.to_owned(),
            found: pointer.format_version,
            supported: POINTER_FORMAT_VERSION,
        });
    }

    if pointer.workspace.is_absolute() {
        return resolve_existing_workspace_path(&pointer.workspace);
    }
    if !relative_allowed {
        return Err(WorkspaceError::RelativeBootstrapPath(path.to_owned()));
    }

    let parent = path
        .parent()
        .ok_or_else(|| WorkspaceError::PointerHasNoParent(path.to_owned()))?;
    let candidate = parent.join(pointer.workspace);
    if !candidate.is_dir() {
        return Err(WorkspaceError::NotDirectory(candidate));
    }
    let canonical = normalize_canonical_path(fs::canonicalize(&candidate).map_err(|source| {
        WorkspaceError::Io {
            operation: "resolve the portable workspace pointer",
            path: candidate,
            source,
        }
    })?);
    WorkspacePath::new(&canonical).map_err(Into::into)
}

#[cfg(windows)]
fn normalize_canonical_path(path: PathBuf) -> PathBuf {
    use std::{
        ffi::OsString,
        os::windows::ffi::{OsStrExt, OsStringExt},
    };

    const VERBATIM_PREFIX: &[u16] = &[92, 92, 63, 92];
    const VERBATIM_UNC_PREFIX: &[u16] = &[92, 92, 63, 92, 85, 78, 67, 92];

    let encoded = path.as_os_str().encode_wide().collect::<Vec<_>>();
    if encoded.starts_with(VERBATIM_UNC_PREFIX) {
        let normalized = [92_u16, 92_u16]
            .into_iter()
            .chain(encoded[VERBATIM_UNC_PREFIX.len()..].iter().copied())
            .collect::<Vec<_>>();
        return PathBuf::from(OsString::from_wide(&normalized));
    }
    if encoded.starts_with(VERBATIM_PREFIX) {
        return PathBuf::from(OsString::from_wide(&encoded[VERBATIM_PREFIX.len()..]));
    }
    path
}

#[cfg(not(windows))]
fn normalize_canonical_path(path: PathBuf) -> PathBuf {
    path
}

fn build_staged_workspace(
    staging: &Path,
    host_triple: HostTriple,
    metadata: &WorkspaceMetadata,
) -> Result<(), WorkspaceError> {
    fs::create_dir(staging).map_err(|source| WorkspaceError::Io {
        operation: "create workspace staging directory",
        path: staging.to_owned(),
        source,
    })?;

    for relative in workspace_layout_directories(host_triple) {
        let directory = staging.join(relative);
        fs::create_dir_all(&directory).map_err(|source| WorkspaceError::Io {
            operation: "create workspace layout directory",
            path: directory,
            source,
        })?;
    }

    let lock_path = staging.join(LOCK_FILE_NAME);
    File::create(&lock_path)
        .and_then(|file| file.sync_all())
        .map_err(|source| WorkspaceError::Io {
            operation: "create workspace lock file",
            path: lock_path,
            source,
        })?;

    initialize_state_database(staging, host_triple, *metadata)?;
    write_json_new(
        &staging.join(METADATA_FILE_NAME),
        metadata,
        "write workspace metadata",
    )
}

fn ensure_host_layout(root: &Path, host_triple: HostTriple) -> Result<(), WorkspaceError> {
    for relative in host_layout_directories(host_triple) {
        let directory = root.join(relative);
        fs::create_dir_all(&directory).map_err(|source| WorkspaceError::Io {
            operation: "prepare the current host workspace layout",
            path: directory,
            source,
        })?;
    }
    Ok(())
}

fn initialize_state_database(
    root: &Path,
    host_triple: HostTriple,
    metadata: WorkspaceMetadata,
) -> Result<(), WorkspaceError> {
    let path = root
        .join("hosts")
        .join(host_triple.as_str())
        .join("data/state.db");
    StateDatabase::open(path, StateDatabaseIdentity::new(metadata, host_triple))?;
    Ok(())
}

fn validate_shared_layout(root: &Path) -> Result<(), WorkspaceError> {
    for relative in [
        PathBuf::from("hosts"),
        PathBuf::from("plugins/packages"),
        PathBuf::from("plugins/staging"),
        PathBuf::from("plugins/active"),
        PathBuf::from("plugins/data"),
        PathBuf::from("plugins/trash"),
    ] {
        let path = root.join(relative);
        let metadata = fs::symlink_metadata(&path).map_err(|source| WorkspaceError::Io {
            operation: "validate workspace layout",
            path: path.clone(),
            source,
        })?;
        if !metadata.file_type().is_dir() {
            return Err(WorkspaceError::InvalidLayoutDirectory(path));
        }
    }
    validate_lock_file(&root.join(LOCK_FILE_NAME))?;
    Ok(())
}

fn validate_lock_operation(operation: &str) -> Result<(), WorkspaceError> {
    if operation.is_empty()
        || operation.len() > 128
        || operation.trim() != operation
        || operation.chars().any(char::is_control)
    {
        return Err(WorkspaceError::InvalidLockOperation(operation.to_owned()));
    }
    Ok(())
}

fn validate_lock_file(path: &Path) -> Result<(), WorkspaceError> {
    let metadata = fs::symlink_metadata(path).map_err(|source| WorkspaceError::Io {
        operation: "validate the workspace lock file",
        path: path.to_owned(),
        source,
    })?;
    if !metadata.file_type().is_file() {
        return Err(WorkspaceError::InvalidLockFile(path.to_owned()));
    }
    Ok(())
}

fn write_lock_holder(path: &Path, holder: &WorkspaceLockHolder) -> Result<(), WorkspaceError> {
    write_json_replacing(path, holder, "write the workspace lock diagnostic")
}

fn read_lock_holder(path: &Path) -> Option<WorkspaceLockHolder> {
    let bytes = fs::read(path).ok()?;
    if bytes.is_empty() {
        return None;
    }
    serde_json::from_slice::<WorkspaceLockHolder>(&bytes)
        .ok()
        .filter(|holder| holder.format_version == LOCK_HOLDER_FORMAT_VERSION)
}

fn workspace_layout_directories(host_triple: HostTriple) -> Vec<PathBuf> {
    let mut directories = vec![
        PathBuf::from("plugins/packages"),
        PathBuf::from("plugins/staging"),
        PathBuf::from("plugins/active"),
        PathBuf::from("plugins/data"),
        PathBuf::from("plugins/trash"),
    ];
    directories.extend(host_layout_directories(host_triple));
    directories
}

fn host_layout_directories(host_triple: HostTriple) -> Vec<PathBuf> {
    let host = PathBuf::from("hosts").join(host_triple.as_str());
    let mut directories: Vec<_> = [
        "software",
        "current",
        "shims",
        "tools",
        "data",
        "cache/catalog",
        "cache/artifacts",
        "staging",
        "trash",
        "logs",
    ]
    .into_iter()
    .map(|relative| host.join(relative))
    .collect();
    directories.push(PathBuf::from("plugins/packages").join(host_triple.as_str()));
    directories
}

fn directory_has_entries(path: &Path) -> Result<bool, WorkspaceError> {
    let mut entries = fs::read_dir(path).map_err(|source| WorkspaceError::Io {
        operation: "inspect the selected workspace directory",
        path: path.to_owned(),
        source,
    })?;
    entries
        .next()
        .transpose()
        .map(|entry| entry.is_some())
        .map_err(|source| WorkspaceError::Io {
            operation: "inspect a workspace directory entry",
            path: path.to_owned(),
            source,
        })
}

fn staging_path(root: &Path, workspace_id: WorkspaceId) -> Result<PathBuf, WorkspaceError> {
    let parent = root
        .parent()
        .ok_or_else(|| WorkspaceError::WorkspaceParentMissing(root.to_owned()))?;
    Ok(parent.join(format!(".softpilot-init-{workspace_id}")))
}

fn cleanup_owned_staging(staging: &Path) {
    if staging
        .file_name()
        .is_some_and(|name| name.to_string_lossy().starts_with(".softpilot-init-"))
    {
        let _ = fs::remove_dir_all(staging);
    }
}

fn read_json<T: for<'de> Deserialize<'de>>(
    path: &Path,
    operation: &'static str,
) -> Result<T, WorkspaceError> {
    let file = File::open(path).map_err(|source| WorkspaceError::Io {
        operation,
        path: path.to_owned(),
        source,
    })?;
    serde_json::from_reader(file).map_err(|source| WorkspaceError::Json {
        operation,
        path: path.to_owned(),
        source,
    })
}

fn write_json_new<T: Serialize>(
    path: &Path,
    value: &T,
    operation: &'static str,
) -> Result<(), WorkspaceError> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|source| WorkspaceError::Io {
            operation,
            path: path.to_owned(),
            source,
        })?;
    serde_json::to_writer_pretty(&mut file, value).map_err(|source| WorkspaceError::Json {
        operation,
        path: path.to_owned(),
        source,
    })?;
    file.write_all(b"\n").map_err(|source| WorkspaceError::Io {
        operation,
        path: path.to_owned(),
        source,
    })?;
    file.sync_all().map_err(|source| WorkspaceError::Io {
        operation,
        path: path.to_owned(),
        source,
    })
}

fn write_json_replacing<T: Serialize>(
    path: &Path,
    value: &T,
    operation: &'static str,
) -> Result<(), WorkspaceError> {
    let parent = path
        .parent()
        .ok_or_else(|| WorkspaceError::PointerHasNoParent(path.to_owned()))?;
    fs::create_dir_all(parent).map_err(|source| WorkspaceError::Io {
        operation,
        path: parent.to_owned(),
        source,
    })?;

    let nonce = Uuid::new_v4();
    let temporary = parent.join(format!(".softpilot-pointer-{nonce}.tmp"));
    let backup = parent.join(format!(".softpilot-pointer-{nonce}.backup"));
    write_json_new(&temporary, value, operation)?;

    let had_existing = path.try_exists().map_err(|source| WorkspaceError::Io {
        operation,
        path: path.to_owned(),
        source,
    })?;
    if had_existing {
        fs::rename(path, &backup).map_err(|source| WorkspaceError::Io {
            operation,
            path: path.to_owned(),
            source,
        })?;
    }

    if let Err(source) = fs::rename(&temporary, path) {
        if had_existing {
            let _ = fs::rename(&backup, path);
        }
        let _ = fs::remove_file(&temporary);
        return Err(WorkspaceError::Io {
            operation,
            path: path.to_owned(),
            source,
        });
    }

    if had_existing {
        fs::remove_file(&backup).map_err(|source| WorkspaceError::Io {
            operation: "remove replaced workspace pointer backup",
            path: backup,
            source,
        })?;
    }
    Ok(())
}

fn current_user_bootstrap_file() -> Option<PathBuf> {
    #[cfg(windows)]
    {
        env::var_os("APPDATA")
            .map(PathBuf::from)
            .map(|path| path.join("SoftPilot/bootstrap.json"))
    }
    #[cfg(target_os = "macos")]
    {
        env::var_os("HOME")
            .map(PathBuf::from)
            .map(|path| path.join("Library/Application Support/SoftPilot/bootstrap.json"))
    }
    #[cfg(target_os = "linux")]
    {
        env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| env::var_os("HOME").map(|path| PathBuf::from(path).join(".config")))
            .map(|path| path.join("softpilot/bootstrap.json"))
    }
    #[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
    {
        None
    }
}

/// Workspace initialization, location, or metadata error.
#[derive(Debug, Error)]
pub enum WorkspaceError {
    /// A core workspace path failed validation.
    #[error(transparent)]
    Path(#[from] WorkspacePathError),
    /// The current platform is outside the supported host matrix.
    #[error(transparent)]
    Host(#[from] HostTripleError),
    /// Workspace metadata requires an explicit migration.
    #[error(transparent)]
    LayoutVersion(#[from] WorkspaceLayoutVersionError),
    /// Host state storage could not be initialized or validated.
    #[error(transparent)]
    Storage(#[from] StorageError),
    /// A file-system operation failed at a known stage.
    #[error("failed to {operation} at '{}': {source}", path.display())]
    Io {
        /// Human-readable operation stage.
        operation: &'static str,
        /// Path involved in the failed operation.
        path: PathBuf,
        /// Operating system error.
        #[source]
        source: io::Error,
    },
    /// JSON could not be read or written at a known stage.
    #[error("failed to {operation} at '{}': {source}", path.display())]
    Json {
        /// Human-readable operation stage.
        operation: &'static str,
        /// Path containing invalid or unwritable JSON.
        path: PathBuf,
        /// Serialization error.
        #[source]
        source: serde_json::Error,
    },
    /// The current executable path unexpectedly has no parent directory.
    #[error("current executable path has no parent directory: '{}'", .0.display())]
    ExecutableHasNoParent(PathBuf),
    /// The configured path is not an accessible directory.
    #[error("workspace path is not an accessible directory: '{}'", .0.display())]
    NotDirectory(PathBuf),
    /// An uninitialized directory contains data the host does not own.
    #[error("workspace directory is not empty and has no workspace.json: '{}'", .0.display())]
    DirectoryNotEmpty(PathBuf),
    /// Workspace metadata is absent.
    #[error("workspace metadata is missing: '{}'", .0.display())]
    MetadataMissing(PathBuf),
    /// A required shared layout path is missing, not a directory, or is a link.
    #[error("workspace layout directory is invalid: '{}'", .0.display())]
    InvalidLayoutDirectory(PathBuf),
    /// The requested workspace parent cannot be resolved safely.
    #[error("workspace parent directory is unavailable: '{}'", .0.display())]
    WorkspaceParentMissing(PathBuf),
    /// The portable or bootstrap pointer has no parent directory.
    #[error("workspace pointer has no parent directory: '{}'", .0.display())]
    PointerHasNoParent(PathBuf),
    /// A user bootstrap must not depend on the process working directory.
    #[error("user workspace bootstrap contains a relative path: '{}'", .0.display())]
    RelativeBootstrapPath(PathBuf),
    /// Pointer metadata is outside the supported compatibility range.
    #[error(
        "workspace pointer '{}' has format version {found}; this host supports {supported}",
        path.display()
    )]
    UnsupportedPointerVersion {
        /// Pointer file containing the incompatible value.
        path: PathBuf,
        /// Version read from the pointer.
        found: u32,
        /// Version supported by this host.
        supported: u32,
    },
    /// The caller supplied an ambiguous or oversized diagnostic operation name.
    #[error("workspace lock operation must be 1-128 trimmed, non-control UTF-8 bytes: '{0}'")]
    InvalidLockOperation(String),
    /// The lock path is not a regular file owned by the workspace layout.
    #[error("workspace lock path is not a regular file: '{}'", .0.display())]
    InvalidLockFile(PathBuf),
    /// Another process retained the workspace lock until the caller's deadline.
    #[error(
        "timed out after {timeout_milliseconds} ms acquiring workspace lock '{}'; holder: {holder:?}",
        path.display()
    )]
    LockTimeout {
        /// Workspace lock-file path.
        path: PathBuf,
        /// Requested timeout converted to milliseconds.
        timeout_milliseconds: u128,
        /// Last readable holder diagnostic, if available.
        holder: Option<WorkspaceLockHolder>,
    },
    /// No supported user configuration root is available.
    #[error("cannot determine the current user's SoftPilot configuration directory")]
    UserConfigDirectoryUnavailable,
    /// System clock cannot produce a valid creation timestamp.
    #[error("system clock is earlier than the Unix epoch")]
    ClockBeforeUnixEpoch,
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TestDirectory {
        path: PathBuf,
    }

    impl TestDirectory {
        fn new(label: &str) -> Self {
            let path =
                env::temp_dir().join(format!("softpilot-engine-test-{label}-{}", Uuid::new_v4()));
            fs::create_dir(&path).expect("create isolated test directory");
            Self { path }
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let expected_prefix = "softpilot-engine-test-";
            if self.path.starts_with(env::temp_dir())
                && self
                    .path
                    .file_name()
                    .is_some_and(|name| name.to_string_lossy().starts_with(expected_prefix))
            {
                let _ = fs::remove_dir_all(&self.path);
            }
        }
    }

    fn service(root: &Path) -> WorkspaceService {
        WorkspaceService::with_locations(
            root.join("bin/softpilot-workspace.json"),
            Some(root.join("config/bootstrap.json")),
            None,
        )
    }

    #[test]
    fn initializes_reads_remembers_and_reuses_a_workspace() {
        let test = TestDirectory::new("initialize");
        let service = service(&test.path);
        let workspace = test.path.join("workspace");

        let initialized = service
            .initialize(&workspace)
            .expect("initialize workspace");
        assert!(initialized.created);
        assert_eq!(initialized.workspace.metadata.layout_version.get(), 1);
        assert!(workspace.join(METADATA_FILE_NAME).is_file());
        assert!(workspace.join("plugins/packages").is_dir());
        assert!(
            workspace
                .join("plugins/packages")
                .join(initialized.workspace.host_triple.as_str())
                .is_dir()
        );
        assert!(
            workspace
                .join("hosts")
                .join(initialized.workspace.host_triple.as_str())
                .join("cache/artifacts")
                .is_dir()
        );
        let state_database = workspace
            .join("hosts")
            .join(initialized.workspace.host_triple.as_str())
            .join("data/state.db");
        assert!(state_database.is_file());
        StateDatabase::open(
            state_database,
            StateDatabaseIdentity::new(
                initialized.workspace.metadata,
                initialized.workspace.host_triple,
            ),
        )
        .expect("reopen initialized state database");

        service
            .remember(&initialized.workspace.path)
            .expect("remember workspace");
        let resolved = service
            .resolve(None)
            .expect("resolve bootstrap")
            .expect("remembered workspace");
        assert_eq!(resolved.source, WorkspaceSource::UserBootstrap);
        assert_eq!(resolved.metadata, initialized.workspace.metadata);

        let repeated = service
            .initialize(&workspace)
            .expect("idempotent initialize");
        assert!(!repeated.created);
        assert_eq!(repeated.workspace.metadata, initialized.workspace.metadata);
    }

    #[test]
    fn refuses_to_claim_a_nonempty_unrecognized_directory() {
        let test = TestDirectory::new("not-empty");
        let workspace = test.path.join("workspace");
        fs::create_dir(&workspace).expect("create workspace candidate");
        let sentinel = workspace.join("user-data.txt");
        fs::write(&sentinel, "preserve").expect("write sentinel");

        let error = service(&test.path)
            .initialize(&workspace)
            .expect_err("must reject nonempty directory");
        assert!(matches!(error, WorkspaceError::DirectoryNotEmpty(_)));
        assert_eq!(
            fs::read_to_string(sentinel).expect("read sentinel"),
            "preserve"
        );
    }

    #[test]
    fn location_precedence_is_explicit_environment_portable_then_bootstrap() {
        let test = TestDirectory::new("precedence");
        let base_service = service(&test.path);
        let explicit = base_service
            .initialize(&test.path.join("explicit"))
            .expect("explicit workspace")
            .workspace;
        let environment = base_service
            .initialize(&test.path.join("environment"))
            .expect("environment workspace")
            .workspace;
        let portable = base_service
            .initialize(&test.path.join("portable"))
            .expect("portable workspace")
            .workspace;
        let bootstrap = base_service
            .initialize(&test.path.join("bootstrap"))
            .expect("bootstrap workspace")
            .workspace;

        base_service
            .remember(&bootstrap.path)
            .expect("write bootstrap pointer");
        let portable_pointer = test.path.join("bin/softpilot-workspace.json");
        fs::create_dir_all(portable_pointer.parent().expect("pointer parent"))
            .expect("create portable directory");
        let relative_portable = WorkspacePointer {
            format_version: POINTER_FORMAT_VERSION,
            workspace: PathBuf::from("../portable"),
        };
        write_json_new(
            &portable_pointer,
            &relative_portable,
            "write portable pointer",
        )
        .expect("write relative portable pointer");

        let configured = WorkspaceService::with_locations(
            portable_pointer.clone(),
            Some(test.path.join("config/bootstrap.json")),
            Some(environment.path.as_path().to_owned()),
        );
        assert_eq!(
            configured
                .locate(Some(explicit.path.as_path()))
                .expect("explicit location")
                .expect("explicit workspace")
                .source,
            WorkspaceSource::Explicit
        );
        assert_eq!(
            configured
                .locate(None)
                .expect("environment location")
                .expect("environment workspace")
                .source,
            WorkspaceSource::Environment
        );

        let without_environment = WorkspaceService::with_locations(
            portable_pointer.clone(),
            Some(test.path.join("config/bootstrap.json")),
            None,
        );
        let located_portable = without_environment
            .locate(None)
            .expect("portable location")
            .expect("portable workspace");
        assert_eq!(located_portable.source, WorkspaceSource::PortablePointer);
        assert_eq!(located_portable.path, portable.path);

        fs::remove_file(portable_pointer).expect("remove portable pointer");
        let located_bootstrap = without_environment
            .locate(None)
            .expect("bootstrap location")
            .expect("bootstrap workspace");
        assert_eq!(located_bootstrap.source, WorkspaceSource::UserBootstrap);
        assert_eq!(located_bootstrap.path, bootstrap.path);
    }

    #[test]
    fn rejects_workspace_metadata_that_requires_migration() {
        let test = TestDirectory::new("layout-version");
        let service = service(&test.path);
        let initialized = service
            .initialize(&test.path.join("workspace"))
            .expect("initialize workspace");
        let metadata_path = initialized
            .workspace
            .path
            .as_path()
            .join(METADATA_FILE_NAME);
        let incompatible = serde_json::json!({
            "layoutVersion": 2,
            "workspaceId": initialized.workspace.metadata.workspace_id,
            "createdAtUnixSeconds": initialized.workspace.metadata.created_at_unix_seconds,
        });
        fs::write(
            &metadata_path,
            serde_json::to_vec_pretty(&incompatible).expect("serialize metadata"),
        )
        .expect("write incompatible metadata");

        let error = service
            .open(&WorkspaceLocation {
                path: initialized.workspace.path,
                source: WorkspaceSource::Explicit,
            })
            .expect_err("must reject future layout");
        assert!(matches!(error, WorkspaceError::LayoutVersion(_)));
    }

    #[test]
    fn lock_timeout_reports_holder_and_releases_cleanly() {
        let test = TestDirectory::new("lock");
        let service = service(&test.path);
        let initialized = service
            .initialize(&test.path.join("workspace"))
            .expect("initialize workspace");
        let workspace = &initialized.workspace.path;

        let guard = service
            .acquire_lock(workspace, "test.holder", Duration::from_secs(1))
            .expect("acquire first lock");
        assert_eq!(guard.holder().operation, "test.holder");
        assert_eq!(guard.holder().process_id, std::process::id());

        let started = Instant::now();
        let error = service
            .acquire_lock(workspace, "test.contender", Duration::from_millis(60))
            .expect_err("competing lock must time out");
        assert!(started.elapsed() >= Duration::from_millis(50));
        match error {
            WorkspaceError::LockTimeout {
                holder: Some(holder),
                ..
            } => {
                assert_eq!(holder.operation, "test.holder");
                assert_eq!(holder.owner_id, guard.holder().owner_id);
            }
            other => panic!("unexpected lock error: {other}"),
        }

        drop(guard);
        assert!(
            fs::read(workspace.as_path().join(LOCK_FILE_NAME))
                .expect("read released lock file")
                .is_empty()
        );
        assert!(!workspace.as_path().join(LOCK_HOLDER_FILE_NAME).exists());
        service
            .acquire_lock(workspace, "test.after-release", Duration::ZERO)
            .expect("lock must be reusable after release");
        assert!(matches!(
            service.acquire_lock(workspace, "", Duration::ZERO),
            Err(WorkspaceError::InvalidLockOperation(_))
        ));
    }

    #[test]
    fn concurrent_initializers_converge_on_one_workspace() {
        use std::sync::{Arc, Barrier};

        let test = TestDirectory::new("concurrent-init");
        let workspace = test.path.join("workspace");
        let barrier = Arc::new(Barrier::new(3));
        let mut workers = Vec::new();
        for _ in 0..2 {
            let service = service(&test.path);
            let workspace = workspace.clone();
            let barrier = Arc::clone(&barrier);
            workers.push(thread::spawn(move || {
                barrier.wait();
                service.initialize(&workspace)
            }));
        }
        barrier.wait();

        let first = workers
            .remove(0)
            .join()
            .expect("first initializer thread")
            .expect("first initializer result");
        let second = workers
            .remove(0)
            .join()
            .expect("second initializer thread")
            .expect("second initializer result");
        assert_ne!(first.created, second.created);
        assert_eq!(first.workspace.metadata, second.workspace.metadata);
    }

    #[cfg(windows)]
    #[test]
    fn normalizes_windows_verbatim_paths_for_persistence_and_display() {
        assert_eq!(
            normalize_canonical_path(PathBuf::from(r"\\?\C:\SoftPilot")),
            PathBuf::from(r"C:\SoftPilot")
        );
        assert_eq!(
            normalize_canonical_path(PathBuf::from(r"\\?\UNC\server\share\SoftPilot")),
            PathBuf::from(r"\\server\share\SoftPilot")
        );
    }
}
