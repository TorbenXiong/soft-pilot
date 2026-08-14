use std::{
    fmt,
    path::{Component, Path, PathBuf},
};

use serde::{Deserialize, Deserializer, Serialize, Serializer, de};
use thiserror::Error;
use uuid::Uuid;

/// An absolute, lexically normalized path selected as a `SoftPilot` workspace.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct WorkspacePath(PathBuf);

impl WorkspacePath {
    /// Validates and lexically normalizes a workspace path without accessing the file system.
    ///
    /// This intentionally does not canonicalize links or require the directory to exist. Workspace
    /// creation and link resolution belong to the host use case that owns the file-system access.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspacePathError`] when the path is empty, relative, contains a parent traversal,
    /// or refers to the file-system root.
    pub fn new(path: &Path) -> Result<Self, WorkspacePathError> {
        if path.as_os_str().is_empty() {
            return Err(WorkspacePathError::Empty);
        }
        if !path.is_absolute() {
            return Err(WorkspacePathError::NotAbsolute);
        }

        let mut normalized = PathBuf::new();
        let mut has_workspace_name = false;
        for component in path.components() {
            match component {
                Component::Prefix(_) | Component::RootDir | Component::Normal(_) => {
                    has_workspace_name |= matches!(component, Component::Normal(_));
                    normalized.push(component.as_os_str());
                }
                Component::CurDir => {}
                Component::ParentDir => return Err(WorkspacePathError::ParentTraversal),
            }
        }

        if !has_workspace_name {
            return Err(WorkspacePathError::FileSystemRoot);
        }
        Ok(Self(normalized))
    }

    /// Returns the normalized native path.
    #[must_use]
    pub fn as_path(&self) -> &Path {
        &self.0
    }

    /// Consumes the value object and returns its normalized native path.
    #[must_use]
    pub fn into_path_buf(self) -> PathBuf {
        self.0
    }
}

impl AsRef<Path> for WorkspacePath {
    fn as_ref(&self) -> &Path {
        self.as_path()
    }
}

impl fmt::Display for WorkspacePath {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.as_path().display().fmt(formatter)
    }
}

impl TryFrom<PathBuf> for WorkspacePath {
    type Error = WorkspacePathError;

    fn try_from(path: PathBuf) -> Result<Self, Self::Error> {
        Self::new(&path)
    }
}

impl TryFrom<&Path> for WorkspacePath {
    type Error = WorkspacePathError;

    fn try_from(path: &Path) -> Result<Self, Self::Error> {
        Self::new(path)
    }
}

impl Serialize for WorkspacePath {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        self.0.serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for WorkspacePath {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let path = PathBuf::deserialize(deserializer)?;
        Self::new(&path).map_err(de::Error::custom)
    }
}

/// Validation error for a workspace path.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum WorkspacePathError {
    /// No path was supplied.
    #[error("workspace path must not be empty")]
    Empty,
    /// Workspace paths must not depend on the process working directory.
    #[error("workspace path must be absolute")]
    NotAbsolute,
    /// Parent traversal would make the stored path ambiguous.
    #[error("workspace path must not contain '..' components")]
    ParentTraversal,
    /// Using an entire file-system root as a workspace is unsafe.
    #[error("workspace path must not be a file-system root")]
    FileSystemRoot,
}

/// Random identifier that remains stable for the lifetime of a workspace.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(transparent)]
pub struct WorkspaceId(Uuid);

impl WorkspaceId {
    /// Generates an identifier using the operating system random source.
    #[must_use]
    pub fn generate() -> Self {
        Self(Uuid::new_v4())
    }

    /// Returns the underlying UUID value.
    #[must_use]
    pub const fn as_uuid(self) -> Uuid {
        self.0
    }
}

impl fmt::Display for WorkspaceId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.0.fmt(formatter)
    }
}

impl std::str::FromStr for WorkspaceId {
    type Err = uuid::Error;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        value.parse().map(Self)
    }
}

/// Version of the workspace directory layout and `workspace.json` contract.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize)]
#[serde(transparent)]
pub struct WorkspaceLayoutVersion(u32);

impl WorkspaceLayoutVersion {
    /// The only layout version this host can currently read and write.
    pub const CURRENT: Self = Self(1);

    /// Creates a version value while reserving zero as invalid metadata.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceLayoutVersionError::Zero`] for version zero.
    pub const fn new(value: u32) -> Result<Self, WorkspaceLayoutVersionError> {
        if value == 0 {
            Err(WorkspaceLayoutVersionError::Zero)
        } else {
            Ok(Self(value))
        }
    }

    /// Returns the persisted numeric representation.
    #[must_use]
    pub const fn get(self) -> u32 {
        self.0
    }

    /// Returns whether the current host can read and write this layout without migration.
    #[must_use]
    pub const fn is_supported(self) -> bool {
        self.0 == Self::CURRENT.0
    }

    /// Rejects older or newer layouts instead of migrating them implicitly.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceLayoutVersionError::Unsupported`] when an explicit migration or a newer
    /// host is required.
    pub const fn ensure_supported(self) -> Result<(), WorkspaceLayoutVersionError> {
        if self.is_supported() {
            Ok(())
        } else {
            Err(WorkspaceLayoutVersionError::Unsupported {
                found: self.0,
                supported: Self::CURRENT.0,
            })
        }
    }
}

impl fmt::Display for WorkspaceLayoutVersion {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.0.fmt(formatter)
    }
}

impl TryFrom<u32> for WorkspaceLayoutVersion {
    type Error = WorkspaceLayoutVersionError;

    fn try_from(value: u32) -> Result<Self, Self::Error> {
        Self::new(value)
    }
}

impl<'de> Deserialize<'de> for WorkspaceLayoutVersion {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let value = u32::deserialize(deserializer)?;
        Self::new(value).map_err(de::Error::custom)
    }
}

/// Validation or compatibility error for a workspace layout version.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum WorkspaceLayoutVersionError {
    /// Zero is reserved for invalid or missing metadata.
    #[error("workspace layout version must be greater than zero")]
    Zero,
    /// The host must not silently migrate an unsupported workspace layout.
    #[error(
        "workspace layout version {found} is unsupported; this host supports version {supported}"
    )]
    Unsupported {
        /// Version read from workspace metadata.
        found: u32,
        /// Layout version supported by this host.
        supported: u32,
    },
}

/// Persistent contents of `<workspace>/workspace.json`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct WorkspaceMetadata {
    /// Workspace directory-layout and metadata contract version.
    pub layout_version: WorkspaceLayoutVersion,
    /// Stable random workspace identity.
    pub workspace_id: WorkspaceId,
    /// Creation time measured in whole seconds since the Unix epoch.
    pub created_at_unix_seconds: u64,
}

impl WorkspaceMetadata {
    /// Creates metadata for the current layout version.
    #[must_use]
    pub const fn new(workspace_id: WorkspaceId, created_at_unix_seconds: u64) -> Self {
        Self {
            layout_version: WorkspaceLayoutVersion::CURRENT,
            workspace_id,
            created_at_unix_seconds,
        }
    }

    /// Rejects metadata that requires an explicit workspace migration.
    ///
    /// # Errors
    ///
    /// Returns [`WorkspaceLayoutVersionError`] for layouts the current host cannot safely use.
    pub const fn ensure_supported(self) -> Result<Self, WorkspaceLayoutVersionError> {
        match self.layout_version.ensure_supported() {
            Ok(()) => Ok(self),
            Err(error) => Err(error),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn workspace_path_normalizes_current_directory_components() {
        let current = std::env::current_dir().expect("current directory");
        let input = current.join("workspace").join(".").join("hosts");
        let expected = current.join("workspace").join("hosts");

        let workspace = WorkspacePath::new(&input).expect("valid workspace path");
        assert_eq!(workspace.as_path(), expected);
    }

    #[test]
    fn workspace_path_rejects_ambiguous_or_dangerous_values() {
        assert_eq!(
            WorkspacePath::new(Path::new("")),
            Err(WorkspacePathError::Empty)
        );
        assert_eq!(
            WorkspacePath::new(Path::new("workspace")),
            Err(WorkspacePathError::NotAbsolute)
        );

        let current = std::env::current_dir().expect("current directory");
        assert_eq!(
            WorkspacePath::new(&current.join("workspace").join("..").join("other")),
            Err(WorkspacePathError::ParentTraversal)
        );

        let root = current
            .ancestors()
            .last()
            .expect("absolute current directory must have a root");
        assert_eq!(
            WorkspacePath::try_from(root),
            Err(WorkspacePathError::FileSystemRoot)
        );
    }

    #[test]
    fn layout_version_requires_explicit_compatibility() {
        assert_eq!(
            WorkspaceLayoutVersion::new(0),
            Err(WorkspaceLayoutVersionError::Zero)
        );
        assert!(WorkspaceLayoutVersion::CURRENT.is_supported());
        assert_eq!(WorkspaceLayoutVersion::CURRENT.ensure_supported(), Ok(()));

        let future = WorkspaceLayoutVersion::new(2).expect("nonzero version");
        assert!(!future.is_supported());
        assert_eq!(
            future.ensure_supported(),
            Err(WorkspaceLayoutVersionError::Unsupported {
                found: 2,
                supported: 1,
            })
        );
    }

    #[test]
    fn workspace_metadata_uses_current_layout_and_stable_id() {
        let id = WorkspaceId::generate();
        let metadata = WorkspaceMetadata::new(id, 1_700_000_000);

        assert_eq!(metadata.layout_version, WorkspaceLayoutVersion::CURRENT);
        assert_eq!(metadata.workspace_id, id);
        assert_eq!(metadata.created_at_unix_seconds, 1_700_000_000);
        assert_eq!(metadata.ensure_supported(), Ok(metadata));
        assert_eq!(id.to_string().parse::<WorkspaceId>(), Ok(id));
    }
}
