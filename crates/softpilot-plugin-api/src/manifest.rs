use std::{collections::BTreeSet, path::Path};

use semver::Version;
use serde::{Deserialize, Serialize};
use softpilot_core::{PlatformTarget, PluginId};
use thiserror::Error;

const SUPPORTED_SCHEMA_VERSION: &str = "0.1.0";
const SUPPORTED_PLUGIN_API: &str = "0.1.0";

/// A validated plugin manifest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PluginManifest {
    /// Manifest schema version.
    pub schema_version: Version,
    /// Stable plugin identifier.
    pub id: PluginId,
    /// Plugin package version.
    pub version: Version,
    /// Required host plugin API version.
    pub plugin_api: Version,
    /// Optional host version requirement expression.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub host_version: Option<String>,
    /// Human-readable name.
    pub name: String,
    /// Human-readable summary.
    pub description: String,
    /// Publisher identity.
    pub publisher: Publisher,
    /// SPDX expression or `LicenseRef` identifier.
    pub license: String,
    /// Category of software managed by this plugin.
    pub kind: PluginKind,
    /// Scope at which the plugin manages software.
    pub management_level: ManagementLevel,
    /// Executable or declarative package entry.
    pub entry: PluginEntry,
    /// Platforms supported by this package.
    pub targets: Vec<PlatformTarget>,
    /// Capabilities requested by the plugin.
    pub permissions: PluginPermissions,
    /// Optional package assets.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub assets: Option<PluginAssets>,
}

impl PluginManifest {
    /// Parses and validates a UTF-8 `plugin.json` document.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] when JSON parsing or semantic validation fails.
    pub fn from_slice(source: &[u8]) -> Result<Self, ManifestError> {
        let manifest: Self = serde_json::from_slice(source)?;
        manifest.validate()?;
        Ok(manifest)
    }

    /// Validates semantic rules that JSON Schema cannot fully express.
    ///
    /// # Errors
    ///
    /// Returns [`ManifestError`] when the manifest cannot be safely handled by this host.
    pub fn validate(&self) -> Result<(), ManifestError> {
        require_supported_version(
            "schemaVersion",
            &self.schema_version,
            SUPPORTED_SCHEMA_VERSION,
        )?;
        require_supported_version("pluginApi", &self.plugin_api, SUPPORTED_PLUGIN_API)?;
        if self.version.to_string().len() > 128 {
            return Err(ManifestError::FieldTooLong {
                field: "version",
                maximum: 128,
            });
        }
        require_bounded("name", &self.name, 80)?;
        require_bounded("description", &self.description, 500)?;
        require_bounded("publisher.name", &self.publisher.name, 120)?;
        require_bounded("license", &self.license, 80)?;
        if let Some(host_version) = &self.host_version {
            require_bounded("hostVersion", host_version, 128)?;
        }
        if let Some(url) = &self.publisher.url {
            validate_https_url(url)?;
        }

        if self.targets.is_empty() {
            return Err(ManifestError::EmptyTargets);
        }
        for target in &self.targets {
            target.validate()?;
        }
        for path in self.entry.paths() {
            validate_relative_package_path(path)?;
        }
        if let Some(assets) = &self.assets {
            for path in assets.paths() {
                validate_relative_package_path(path)?;
            }
            ensure_unique("assets.locales", assets.locales.iter().cloned())?;
        }
        for origin in &self.permissions.network.catalog_origins {
            validate_https_origin(origin)?;
        }
        for origin in &self.permissions.network.artifact_origins {
            validate_https_origin(origin)?;
        }

        ensure_unique(
            "targets",
            self.targets.iter().map(|item| format!("{item:?}")),
        )?;
        ensure_unique(
            "permissions.network.catalogOrigins",
            self.permissions.network.catalog_origins.iter().cloned(),
        )?;
        ensure_unique(
            "permissions.network.artifactOrigins",
            self.permissions.network.artifact_origins.iter().cloned(),
        )?;
        ensure_unique(
            "permissions.process",
            self.permissions
                .process
                .iter()
                .map(|item| format!("{item:?}")),
        )?;
        ensure_unique(
            "permissions.shell",
            self.permissions
                .shell
                .iter()
                .map(|item| format!("{item:?}")),
        )?;
        ensure_unique(
            "permissions.os",
            self.permissions.os.iter().map(|item| format!("{item:?}")),
        )?;

        Ok(())
    }

    /// Returns all entry and asset paths declared by the manifest.
    pub(crate) fn package_paths(&self) -> impl Iterator<Item = &str> {
        self.entry
            .paths()
            .chain(self.assets.iter().flat_map(PluginAssets::paths))
    }
}

/// Publisher metadata.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Publisher {
    /// Publisher display name.
    pub name: String,
    /// Optional HTTPS publisher page.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
}

/// Software category managed by a plugin.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PluginKind {
    /// Language or execution runtime.
    Runtime,
    /// User-facing application.
    Application,
    /// Compiler, SDK or build toolchain.
    Toolchain,
    /// Long-running local service.
    Service,
    /// Package delegated to an operating-system installer.
    SystemPackage,
}

/// Software management scope.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ManagementLevel {
    /// Fully isolated under the selected workspace.
    Workspace,
    /// Installed into the current user's profile.
    User,
    /// Installed machine-wide.
    System,
    /// Discovered and observed but not owned by `SoftPilot`.
    External,
}

/// Plugin package entry point.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "kebab-case", deny_unknown_fields)]
pub enum PluginEntry {
    /// A purely declarative plugin.
    Recipe {
        /// Declarative recipe path.
        recipe: String,
    },
    /// A recipe augmented by a capability-free Wasm Component.
    Component {
        /// Declarative recipe path.
        recipe: String,
        /// Wasm Component path.
        component: String,
    },
}

impl PluginEntry {
    fn paths(&self) -> impl Iterator<Item = &str> {
        let (recipe, component) = match self {
            Self::Recipe { recipe } => (recipe.as_str(), None),
            Self::Component { recipe, component } => (recipe.as_str(), Some(component.as_str())),
        };
        std::iter::once(recipe).chain(component)
    }

    /// Returns the Wasm Component path, when declared.
    #[must_use]
    pub fn component(&self) -> Option<&str> {
        match self {
            Self::Recipe { .. } => None,
            Self::Component { component, .. } => Some(component),
        }
    }
}

/// Capabilities requested by a plugin.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PluginPermissions {
    /// Network allowlists.
    pub network: NetworkPermissions,
    /// Process execution scopes available to host-executed plans.
    pub process: Vec<ProcessPermission>,
    /// Shell contributions available to activation plans.
    pub shell: Vec<ShellPermission>,
    /// Operating-system integration capabilities.
    pub os: Vec<OperatingSystemPermission>,
}

/// HTTPS origins a plugin may use for metadata and artifacts.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct NetworkPermissions {
    /// Metadata endpoint origins.
    pub catalog_origins: Vec<String>,
    /// Artifact endpoint origins.
    pub artifact_origins: Vec<String>,
}

/// Process execution scopes available to a plugin plan.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ProcessPermission {
    /// Execute inside the isolated staging directory.
    Staged,
    /// Execute from an installed instance.
    Installed,
}

/// Shell contribution capabilities.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ShellPermission {
    /// Add workspace-managed PATH entries.
    Path,
    /// Add workspace-managed environment variables.
    Environment,
    /// Add workspace-managed command shims.
    Shims,
}

/// Operating-system integration capabilities.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum OperatingSystemPermission {
    /// Create a shortcut.
    Shortcut,
    /// Request an explicitly approved elevated operation.
    Elevation,
    /// Invoke a host-controlled native system installer.
    SystemInstaller,
}

/// Optional files carried alongside plugin code and recipes.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PluginAssets {
    /// Plugin icon path.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub icon: Option<String>,
    /// Localization resource paths.
    #[serde(default)]
    pub locales: Vec<String>,
    /// Software bill of materials path.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sbom: Option<String>,
}

impl PluginAssets {
    fn paths(&self) -> impl Iterator<Item = &str> {
        self.icon
            .iter()
            .chain(self.locales.iter())
            .chain(self.sbom.iter())
            .map(String::as_str)
    }
}

/// Manifest parsing or semantic validation failure.
#[derive(Debug, Error)]
pub enum ManifestError {
    /// Invalid JSON or a schema-shape mismatch.
    #[error("invalid plugin.json: {0}")]
    Json(#[from] serde_json::Error),
    /// The host does not support this manifest/API version.
    #[error("unsupported {field} {actual}; this host supports {supported}")]
    UnsupportedVersion {
        /// Field being checked.
        field: &'static str,
        /// Value found in the manifest.
        actual: Version,
        /// Version supported by this host.
        supported: &'static str,
    },
    /// A required display field was blank.
    #[error("{0} must not be blank")]
    BlankField(&'static str),
    /// A string field exceeded its schema limit.
    #[error("{field} exceeds the maximum length of {maximum} characters")]
    FieldTooLong {
        /// Field being checked.
        field: &'static str,
        /// Maximum character count.
        maximum: usize,
    },
    /// No target was declared.
    #[error("targets must contain at least one platform")]
    EmptyTargets,
    /// A package path was absolute or could escape the package root.
    #[error("unsafe package-relative path: {0}")]
    UnsafePath(String),
    /// A network permission was not a strict HTTPS origin.
    #[error("network permission must be an HTTPS origin without path, query, or fragment: {0}")]
    InvalidHttpsOrigin(String),
    /// A publisher URL was not HTTPS.
    #[error("publisher URL must be an absolute HTTPS URL: {0}")]
    InvalidHttpsUrl(String),
    /// A list contained a duplicate value.
    #[error("{field} contains duplicate value: {value}")]
    DuplicateValue {
        /// Field containing the duplicate.
        field: &'static str,
        /// Duplicate value.
        value: String,
    },
    /// A target used an invalid OS/libc combination.
    #[error(transparent)]
    Platform(#[from] softpilot_core::PlatformTargetError),
}

fn require_supported_version(
    field: &'static str,
    actual: &Version,
    supported: &'static str,
) -> Result<(), ManifestError> {
    if actual.to_string() == supported {
        Ok(())
    } else {
        Err(ManifestError::UnsupportedVersion {
            field,
            actual: actual.clone(),
            supported,
        })
    }
}

fn require_nonempty(field: &'static str, value: &str) -> Result<(), ManifestError> {
    if value.trim().is_empty() {
        Err(ManifestError::BlankField(field))
    } else {
        Ok(())
    }
}

fn require_bounded(field: &'static str, value: &str, maximum: usize) -> Result<(), ManifestError> {
    require_nonempty(field, value)?;
    if value.chars().count() > maximum {
        Err(ManifestError::FieldTooLong { field, maximum })
    } else {
        Ok(())
    }
}

fn ensure_unique(
    field: &'static str,
    values: impl Iterator<Item = String>,
) -> Result<(), ManifestError> {
    let mut seen = BTreeSet::new();
    for value in values {
        if !seen.insert(value.clone()) {
            return Err(ManifestError::DuplicateValue { field, value });
        }
    }
    Ok(())
}

pub(crate) fn validate_relative_package_path(value: &str) -> Result<(), ManifestError> {
    if value.is_empty()
        || value.len() > 240
        || value.contains('\\')
        || value.contains('\0')
        || value.starts_with('/')
        || value.ends_with('/')
        || Path::new(value).is_absolute()
        || !value
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || "._-/".contains(character))
        || !value
            .chars()
            .next()
            .is_some_and(|character| character.is_ascii_alphanumeric())
        || value
            .split('/')
            .any(|segment| segment.is_empty() || matches!(segment, "." | ".."))
    {
        return Err(ManifestError::UnsafePath(value.to_owned()));
    }
    Ok(())
}

fn validate_https_url(value: &str) -> Result<(), ManifestError> {
    let Some(remainder) = value.strip_prefix("https://") else {
        return Err(ManifestError::InvalidHttpsUrl(value.to_owned()));
    };
    let authority = remainder.split(['/', '?', '#']).next().unwrap_or_default();
    if !is_valid_https_authority(authority) || value.chars().any(char::is_whitespace) {
        return Err(ManifestError::InvalidHttpsUrl(value.to_owned()));
    }
    Ok(())
}

fn validate_https_origin(value: &str) -> Result<(), ManifestError> {
    let Some(authority) = value.strip_prefix("https://") else {
        return Err(ManifestError::InvalidHttpsOrigin(value.to_owned()));
    };
    if authority.contains(['/', '?', '#'])
        || authority.chars().any(char::is_whitespace)
        || !is_valid_https_authority(authority)
    {
        return Err(ManifestError::InvalidHttpsOrigin(value.to_owned()));
    }

    Ok(())
}

fn is_valid_https_authority(authority: &str) -> bool {
    if authority.is_empty() || authority.contains('@') {
        return false;
    }
    let (host, port) = authority
        .rsplit_once(':')
        .map_or((authority, None), |(host, port)| (host, Some(port)));
    !(host.is_empty()
        || host.starts_with(['.', '-'])
        || host.ends_with(['.', '-'])
        || !host
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || matches!(character, '.' | '-'))
        || port.is_some_and(|value| value.is_empty() || !value.chars().all(|c| c.is_ascii_digit())))
}

/// A package entry consisting of a normalized path and its uncompressed size.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PackageEntry {
    /// Forward-slash-separated package-relative path.
    pub path: String,
    /// Uncompressed byte count declared by the ZIP container.
    pub uncompressed_size: u64,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn valid_manifest() -> Vec<u8> {
        br#"{
          "schemaVersion": "0.1.0",
          "id": "dev.softpilot.node",
          "version": "1.2.3",
          "pluginApi": "0.1.0",
          "name": "Node.js",
          "description": "Official Node.js runtime",
          "publisher": { "name": "SoftPilot", "url": "https://softpilot.dev/plugins" },
          "license": "MIT",
          "kind": "runtime",
          "managementLevel": "workspace",
          "entry": { "type": "recipe", "recipe": "recipe.json" },
          "targets": [{ "os": "windows", "architecture": "x86-64" }],
          "permissions": {
            "network": {
              "catalogOrigins": ["https://nodejs.org"],
              "artifactOrigins": ["https://nodejs.org"]
            },
            "process": [],
            "shell": ["path", "shims"],
            "os": []
          }
        }"#
        .to_vec()
    }

    #[test]
    fn parses_a_valid_manifest() {
        let manifest = PluginManifest::from_slice(&valid_manifest()).expect("valid manifest");
        assert_eq!(manifest.id.as_str(), "dev.softpilot.node");
    }

    #[test]
    fn rejects_unknown_fields() {
        let mut value: serde_json::Value =
            serde_json::from_slice(&valid_manifest()).expect("fixture JSON");
        value["surprise"] = serde_json::Value::Bool(true);
        let bytes = serde_json::to_vec(&value).expect("serialize fixture");
        assert!(matches!(
            PluginManifest::from_slice(&bytes),
            Err(ManifestError::Json(_))
        ));
    }

    #[test]
    fn rejects_path_traversal_and_non_https_origins() {
        assert!(validate_relative_package_path("../payload.exe").is_err());
        assert!(validate_https_origin("http://example.com").is_err());
        assert!(validate_https_origin("https://example.com/path").is_err());
    }
}
