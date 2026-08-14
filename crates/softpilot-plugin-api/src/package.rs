use std::{
    collections::BTreeSet,
    fs::File,
    io::{Read, Seek},
    path::{Path, PathBuf},
};

use serde::Serialize;
use thiserror::Error;
use zip::ZipArchive;

#[cfg(feature = "component-validation")]
use crate::component::validate_component;
use crate::manifest::{
    ManifestError, PackageEntry, PluginManifest, validate_relative_package_path,
};

const MAX_ARCHIVE_ENTRIES: usize = 256;
const MAX_ARCHIVE_UNCOMPRESSED_BYTES: u64 = 512 * 1024 * 1024;
const MAX_ENTRY_UNCOMPRESSED_BYTES: u64 = 256 * 1024 * 1024;
const MAX_MANIFEST_BYTES: u64 = 256 * 1024;
const MAX_COMPONENT_BYTES: u64 = 32 * 1024 * 1024;

/// Result of validating a local plugin package without installing it.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InspectedPlugin {
    /// Inspected package path.
    pub package_path: PathBuf,
    /// Parsed and validated manifest.
    pub manifest: PluginManifest,
    /// Files declared by the ZIP central directory.
    pub entries: Vec<PackageEntry>,
    /// Whether a declared Wasm Component was validated.
    pub component_validated: bool,
}

/// Read-only validator for `.softpilot-plugin` packages.
pub struct PluginPackageInspector;

impl PluginPackageInspector {
    /// Opens and validates a package without extracting or executing it.
    ///
    /// # Errors
    ///
    /// Returns [`PackageError`] when the ZIP, manifest, declared files, or component is invalid.
    pub fn inspect(path: impl AsRef<Path>) -> Result<InspectedPlugin, PackageError> {
        let path = path.as_ref();
        let file = File::open(path).map_err(|source| PackageError::Open {
            path: path.to_path_buf(),
            source,
        })?;
        let mut archive = ZipArchive::new(file)?;
        if archive.len() > MAX_ARCHIVE_ENTRIES {
            return Err(PackageError::TooManyEntries {
                actual: archive.len(),
                maximum: MAX_ARCHIVE_ENTRIES,
            });
        }

        let entries = inspect_entries(&mut archive)?;
        let manifest_bytes = read_entry(&mut archive, "plugin.json", MAX_MANIFEST_BYTES)?;
        let manifest = PluginManifest::from_slice(&manifest_bytes)?;

        let available: BTreeSet<&str> = entries.iter().map(|item| item.path.as_str()).collect();
        for required in manifest.package_paths() {
            if !available.contains(required) {
                return Err(PackageError::MissingDeclaredEntry(required.to_owned()));
            }
        }

        let component_validated = if let Some(component_path) = manifest.entry.component() {
            let component_bytes = read_entry(&mut archive, component_path, MAX_COMPONENT_BYTES)?;
            validate_component_if_enabled(&component_bytes)?
        } else {
            false
        };

        Ok(InspectedPlugin {
            package_path: path.to_path_buf(),
            manifest,
            entries,
            component_validated,
        })
    }
}

#[cfg(feature = "component-validation")]
fn validate_component_if_enabled(bytes: &[u8]) -> Result<bool, PackageError> {
    validate_component(bytes).map_err(|error| PackageError::InvalidComponent(error.to_string()))?;
    Ok(true)
}

#[cfg(not(feature = "component-validation"))]
fn validate_component_if_enabled(_bytes: &[u8]) -> Result<bool, PackageError> {
    Err(PackageError::ComponentValidationUnavailable)
}

fn inspect_entries<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
) -> Result<Vec<PackageEntry>, PackageError> {
    let mut entries = Vec::with_capacity(archive.len());
    let mut names = BTreeSet::new();
    let mut total_size = 0_u64;
    for index in 0..archive.len() {
        let entry = archive.by_index(index)?;
        let name = entry.name().to_owned();
        validate_relative_package_path(name.trim_end_matches('/'))?;
        if entry.is_dir() {
            continue;
        }
        if entry
            .unix_mode()
            .is_some_and(|mode| mode & 0o170_000 == 0o120_000)
        {
            return Err(PackageError::UnsupportedEntry(name));
        }
        if entry.size() > MAX_ENTRY_UNCOMPRESSED_BYTES {
            return Err(PackageError::EntryTooLarge {
                path: name,
                actual: entry.size(),
                maximum: MAX_ENTRY_UNCOMPRESSED_BYTES,
            });
        }
        total_size = total_size
            .checked_add(entry.size())
            .ok_or(PackageError::ArchiveTooLarge {
                actual: u64::MAX,
                maximum: MAX_ARCHIVE_UNCOMPRESSED_BYTES,
            })?;
        if total_size > MAX_ARCHIVE_UNCOMPRESSED_BYTES {
            return Err(PackageError::ArchiveTooLarge {
                actual: total_size,
                maximum: MAX_ARCHIVE_UNCOMPRESSED_BYTES,
            });
        }
        if !names.insert(name.clone()) {
            return Err(PackageError::DuplicateEntry(name));
        }
        entries.push(PackageEntry {
            path: name,
            uncompressed_size: entry.size(),
        });
    }
    Ok(entries)
}

fn read_entry<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
    name: &str,
    maximum_size: u64,
) -> Result<Vec<u8>, PackageError> {
    let entry = archive
        .by_name(name)
        .map_err(|_| PackageError::MissingEntry(name.to_owned()))?;
    if entry.is_dir() {
        return Err(PackageError::MissingEntry(name.to_owned()));
    }
    if entry.size() > maximum_size {
        return Err(PackageError::EntryTooLarge {
            path: name.to_owned(),
            actual: entry.size(),
            maximum: maximum_size,
        });
    }

    let capacity = usize::try_from(entry.size()).unwrap_or(0);
    let mut bytes = Vec::with_capacity(capacity);
    entry
        .take(maximum_size + 1)
        .read_to_end(&mut bytes)
        .map_err(PackageError::Read)?;
    if u64::try_from(bytes.len()).unwrap_or(u64::MAX) > maximum_size {
        return Err(PackageError::EntryTooLarge {
            path: name.to_owned(),
            actual: u64::try_from(bytes.len()).unwrap_or(u64::MAX),
            maximum: maximum_size,
        });
    }
    Ok(bytes)
}

/// Plugin package validation failure.
#[derive(Debug, Error)]
pub enum PackageError {
    /// The package file could not be opened.
    #[error("failed to open plugin package {path}: {source}")]
    Open {
        /// Requested package path.
        path: PathBuf,
        /// Filesystem error.
        source: std::io::Error,
    },
    /// The ZIP container is malformed.
    #[error("invalid plugin package ZIP: {0}")]
    Zip(#[from] zip::result::ZipError),
    /// A package entry could not be read.
    #[error("failed to read plugin package entry: {0}")]
    Read(std::io::Error),
    /// The archive contains more files than the host permits.
    #[error("plugin package contains {actual} entries; maximum is {maximum}")]
    TooManyEntries {
        /// Entry count found.
        actual: usize,
        /// Host limit.
        maximum: usize,
    },
    /// The ZIP contains the same path more than once.
    #[error("plugin package contains duplicate entry: {0}")]
    DuplicateEntry(String),
    /// The ZIP contains an unsupported special entry such as a symbolic link.
    #[error("plugin package contains unsupported special entry: {0}")]
    UnsupportedEntry(String),
    /// A mandatory package file is absent.
    #[error("plugin package is missing entry: {0}")]
    MissingEntry(String),
    /// An entry declared by the manifest is absent.
    #[error("plugin manifest declares a missing entry: {0}")]
    MissingDeclaredEntry(String),
    /// An entry exceeded the host's bounded-read limit.
    #[error("plugin package entry {path} is {actual} bytes; maximum is {maximum}")]
    EntryTooLarge {
        /// Entry path.
        path: String,
        /// Size found.
        actual: u64,
        /// Host limit.
        maximum: u64,
    },
    /// Declared total uncompressed content exceeds the inspection limit.
    #[error("plugin package declares {actual} uncompressed bytes; maximum is {maximum}")]
    ArchiveTooLarge {
        /// Total uncompressed size found.
        actual: u64,
        /// Host limit.
        maximum: u64,
    },
    /// The manifest is invalid.
    #[error(transparent)]
    Manifest(#[from] ManifestError),
    /// The Wasm Component is invalid.
    #[error("{0}")]
    InvalidComponent(String),
    /// This build intentionally omitted the Wasm Component validator.
    #[error("this SoftPilot build does not include Wasm Component validation")]
    ComponentValidationUnavailable,
}

#[cfg(test)]
mod tests {
    use std::{
        fs::{self, File},
        io::Write,
        sync::atomic::{AtomicU64, Ordering},
        time::{SystemTime, UNIX_EPOCH},
    };

    use zip::{ZipWriter, write::SimpleFileOptions};

    use super::*;

    static NEXT_TEST_ID: AtomicU64 = AtomicU64::new(0);

    #[test]
    fn inspects_a_recipe_plugin_without_extracting_it() {
        let path = temporary_package_path();
        let file = File::create(&path).expect("create package");
        let mut archive = ZipWriter::new(file);
        let options = SimpleFileOptions::default();
        archive
            .start_file("plugin.json", options)
            .expect("start manifest");
        archive
            .write_all(valid_manifest().as_bytes())
            .expect("write manifest");
        archive
            .start_file("recipe.json", options)
            .expect("start recipe");
        archive.write_all(b"{}").expect("write recipe");
        archive.finish().expect("finish package");

        let inspected = PluginPackageInspector::inspect(&path).expect("valid package");
        assert_eq!(inspected.manifest.id.as_str(), "dev.softpilot.fixture");
        assert!(!inspected.component_validated);

        fs::remove_file(path).expect("remove package");
    }

    fn temporary_package_path() -> PathBuf {
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock after Unix epoch")
            .as_nanos();
        let sequence = NEXT_TEST_ID.fetch_add(1, Ordering::Relaxed);
        std::env::temp_dir().join(format!(
            "softpilot-{}-{timestamp}-{sequence}.softpilot-plugin",
            std::process::id()
        ))
    }

    fn valid_manifest() -> &'static str {
        r#"{
          "schemaVersion": "0.1.0",
          "id": "dev.softpilot.fixture",
          "version": "1.0.0",
          "pluginApi": "0.1.0",
          "name": "Fixture",
          "description": "Test fixture",
          "publisher": { "name": "SoftPilot" },
          "license": "MIT",
          "kind": "application",
          "managementLevel": "workspace",
          "entry": { "type": "recipe", "recipe": "recipe.json" },
          "targets": [{ "os": "windows", "architecture": "x86-64" }],
          "permissions": {
            "network": { "catalogOrigins": [], "artifactOrigins": [] },
            "process": [],
            "shell": [],
            "os": []
          }
        }"#
    }
}
