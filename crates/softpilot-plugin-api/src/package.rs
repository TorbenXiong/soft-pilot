use std::{
    collections::{BTreeMap, BTreeSet},
    fs::{self, File},
    io::{Read, Seek, SeekFrom},
    path::{Path, PathBuf},
};

use serde::Serialize;
use sha2::{Digest, Sha256};
use thiserror::Error;
use zip::ZipArchive;

#[cfg(feature = "component-validation")]
use crate::component::validate_component;
use crate::manifest::{
    ManifestError, PackageEntry, PluginManifest, validate_relative_package_path,
};

const MAX_ARCHIVE_ENTRIES: usize = 256;
const MAX_ARCHIVE_UNCOMPRESSED_BYTES: u64 = 512 * 1024 * 1024;
const MAX_ARCHIVE_FILE_BYTES: u64 = 512 * 1024 * 1024;
const MAX_ENTRY_UNCOMPRESSED_BYTES: u64 = 256 * 1024 * 1024;
const MAX_MANIFEST_BYTES: u64 = 256 * 1024;
const MAX_COMPONENT_BYTES: u64 = 32 * 1024 * 1024;

/// Result of validating a local plugin package without installing it.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InspectedPlugin {
    /// Inspected package path.
    pub package_path: PathBuf,
    /// Complete package byte length.
    pub package_size_bytes: u64,
    /// Lowercase SHA-256 of the complete package file.
    pub package_sha256: String,
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
        let path_metadata = fs::symlink_metadata(path).map_err(|source| PackageError::Open {
            path: path.to_path_buf(),
            source,
        })?;
        if !path_metadata.file_type().is_file() {
            return Err(PackageError::NotRegularFile(path.to_owned()));
        }

        let mut file = File::open(path).map_err(|source| PackageError::Open {
            path: path.to_path_buf(),
            source,
        })?;
        file.lock_shared()
            .map_err(|source| PackageError::InspectIo {
                operation: "lock the plugin package for inspection",
                path: path.to_owned(),
                source,
            })?;
        let package_size_bytes = file
            .metadata()
            .map_err(|source| PackageError::InspectIo {
                operation: "read plugin package metadata",
                path: path.to_owned(),
                source,
            })?
            .len();
        if package_size_bytes > MAX_ARCHIVE_FILE_BYTES {
            return Err(PackageError::PackageFileTooLarge {
                actual: package_size_bytes,
                maximum: MAX_ARCHIVE_FILE_BYTES,
            });
        }
        let (hashed_size, package_sha256) =
            sha256_reader((&mut file).take(MAX_ARCHIVE_FILE_BYTES + 1)).map_err(|source| {
                PackageError::InspectIo {
                    operation: "hash the plugin package",
                    path: path.to_owned(),
                    source,
                }
            })?;
        if hashed_size > MAX_ARCHIVE_FILE_BYTES {
            return Err(PackageError::PackageFileTooLarge {
                actual: hashed_size,
                maximum: MAX_ARCHIVE_FILE_BYTES,
            });
        }
        if hashed_size != package_size_bytes {
            return Err(PackageError::PackageChangedDuringInspection {
                expected: package_size_bytes,
                actual: hashed_size,
            });
        }
        file.seek(SeekFrom::Start(0))
            .map_err(|source| PackageError::InspectIo {
                operation: "rewind the plugin package after hashing",
                path: path.to_owned(),
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
            package_size_bytes,
            package_sha256,
            manifest,
            entries,
            component_validated,
        })
    }
}

fn sha256_reader(mut reader: impl Read) -> std::io::Result<(u64, String)> {
    let mut hasher = Sha256::new();
    let mut buffer = vec![0_u8; 64 * 1024].into_boxed_slice();
    let mut size = 0_u64;
    loop {
        let read = reader.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        size = size.saturating_add(u64::try_from(read).unwrap_or(u64::MAX));
        hasher.update(&buffer[..read]);
    }
    Ok((size, format!("{:x}", hasher.finalize())))
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
    let mut paths = BTreeMap::new();
    let mut total_size = 0_u64;
    for index in 0..archive.len() {
        let entry = archive.by_index(index)?;
        let name = entry.name().to_owned();
        let is_directory = entry.is_dir();
        let normalized = if is_directory {
            name.strip_suffix('/').unwrap_or_default()
        } else {
            name.as_str()
        };
        validate_relative_package_path(normalized)?;
        if entry.is_symlink() || !is_supported_unix_mode(entry.unix_mode(), is_directory) {
            return Err(PackageError::UnsupportedEntry(name));
        }
        register_portable_path(&mut paths, normalized, is_directory)?;
        if is_directory {
            continue;
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
        entries.push(PackageEntry {
            path: name,
            uncompressed_size: entry.size(),
        });
    }
    Ok(entries)
}

fn is_supported_unix_mode(mode: Option<u32>, is_directory: bool) -> bool {
    const FILE_TYPE_MASK: u32 = 0o170_000;
    const REGULAR_FILE: u32 = 0o100_000;
    const DIRECTORY: u32 = 0o040_000;

    match mode.map(|value| value & FILE_TYPE_MASK) {
        None | Some(0) => true,
        Some(REGULAR_FILE) => !is_directory,
        Some(DIRECTORY) => is_directory,
        Some(_) => false,
    }
}

fn register_portable_path(
    paths: &mut BTreeMap<String, (String, bool)>,
    path: &str,
    is_directory: bool,
) -> Result<(), PackageError> {
    let key = path.to_ascii_lowercase();
    if let Some((existing, _)) = paths.get(&key) {
        return Err(PackageError::DuplicateEntry(format!(
            "{existing} conflicts with {path}"
        )));
    }

    for (existing_key, (existing, existing_is_directory)) in paths.iter() {
        let existing_is_parent = key
            .strip_prefix(existing_key)
            .is_some_and(|suffix| suffix.starts_with('/'));
        let new_is_parent = existing_key
            .strip_prefix(&key)
            .is_some_and(|suffix| suffix.starts_with('/'));
        if (existing_is_parent && !existing_is_directory) || (new_is_parent && !is_directory) {
            return Err(PackageError::ConflictingEntry {
                first: existing.clone(),
                second: path.to_owned(),
            });
        }
    }

    paths.insert(key, (path.to_owned(), is_directory));
    Ok(())
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
    /// The package path is a directory, link, or special file.
    #[error("plugin package path is not a regular file: '{}'", .0.display())]
    NotRegularFile(PathBuf),
    /// Inspection I/O failed after the package was opened.
    #[error("failed to {operation} at '{}': {source}", path.display())]
    InspectIo {
        /// Human-readable inspection stage.
        operation: &'static str,
        /// Package path.
        path: PathBuf,
        /// Operating-system error.
        source: std::io::Error,
    },
    /// The compressed package file exceeded the inspection limit.
    #[error("plugin package file is {actual} bytes; maximum is {maximum}")]
    PackageFileTooLarge {
        /// File bytes observed.
        actual: u64,
        /// Maximum package file bytes.
        maximum: u64,
    },
    /// The package length changed while the locked file handle was being hashed.
    #[error("plugin package changed during inspection: expected {expected} bytes, read {actual}")]
    PackageChangedDuringInspection {
        /// Length read from the locked handle metadata.
        expected: u64,
        /// Bytes read by the SHA-256 pass.
        actual: u64,
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
    /// Two portable paths cannot coexist as files and directories.
    #[error("plugin package contains conflicting entries: {first} and {second}")]
    ConflictingEntry {
        /// Previously observed entry.
        first: String,
        /// Conflicting entry.
        second: String,
    },
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
        io::{Cursor, Write},
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
        assert_eq!(
            inspected.package_size_bytes,
            fs::metadata(&path).expect("package metadata").len()
        );
        assert_eq!(inspected.package_sha256.len(), 64);

        fs::remove_file(path).expect("remove package");
    }

    #[test]
    fn computes_the_standard_sha256_vector() {
        let (size, digest) =
            sha256_reader(Cursor::new(b"abc")).expect("hash in-memory package bytes");
        assert_eq!(size, 3);
        assert_eq!(
            digest,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn rejects_case_folded_duplicates_and_file_prefix_conflicts() {
        let duplicate = write_test_package(&[
            ("plugin.json", valid_manifest().as_bytes()),
            ("recipe.json", b"{}"),
            ("Assets/icon.svg", b"first"),
            ("assets/icon.svg", b"second"),
        ]);
        assert!(matches!(
            PluginPackageInspector::inspect(&duplicate),
            Err(PackageError::DuplicateEntry(_))
        ));
        fs::remove_file(duplicate).expect("remove duplicate package");

        let conflict = write_test_package(&[
            ("plugin.json", valid_manifest().as_bytes()),
            ("recipe.json", b"{}"),
            ("payload", b"file"),
            ("payload/tool.exe", b"nested"),
        ]);
        assert!(matches!(
            PluginPackageInspector::inspect(&conflict),
            Err(PackageError::ConflictingEntry { .. })
        ));
        fs::remove_file(conflict).expect("remove conflicting package");
    }

    #[test]
    fn rejects_special_entries_and_missing_manifest_paths() {
        assert!(is_supported_unix_mode(Some(0o100_644), false));
        assert!(is_supported_unix_mode(Some(0o040_755), true));
        assert!(!is_supported_unix_mode(Some(0o010_644), false));
        assert!(!is_supported_unix_mode(Some(0o020_644), false));
        assert!(!is_supported_unix_mode(Some(0o060_644), false));
        assert!(!is_supported_unix_mode(Some(0o120_777), false));
        assert!(!is_supported_unix_mode(Some(0o140_777), false));

        let missing = write_test_package(&[("plugin.json", valid_manifest().as_bytes())]);
        assert!(matches!(
            PluginPackageInspector::inspect(&missing),
            Err(PackageError::MissingDeclaredEntry(path)) if path == "recipe.json"
        ));
        fs::remove_file(missing).expect("remove missing-entry package");

        let symlink = temporary_package_path();
        let file = File::create(&symlink).expect("create symlink package");
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
        archive
            .add_symlink("payload-link", "recipe.json", options)
            .expect("add symlink");
        archive.finish().expect("finish symlink package");
        assert!(matches!(
            PluginPackageInspector::inspect(&symlink),
            Err(PackageError::UnsupportedEntry(path)) if path == "payload-link"
        ));
        fs::remove_file(symlink).expect("remove symlink package");
    }

    fn write_test_package(entries: &[(&str, &[u8])]) -> PathBuf {
        let path = temporary_package_path();
        let file = File::create(&path).expect("create test package");
        let mut archive = ZipWriter::new(file);
        let options = SimpleFileOptions::default();
        for (name, contents) in entries {
            archive
                .start_file(*name, options)
                .expect("start test entry");
            archive.write_all(contents).expect("write test entry");
        }
        archive.finish().expect("finish test package");
        path
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
