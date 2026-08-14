use semver::{Version, VersionReq};
use serde::Serialize;
use softpilot_core::{HostTriple, HostTripleError};
use thiserror::Error;

use crate::{PluginManifest, manifest::SUPPORTED_PLUGIN_API};

/// Host facts used to evaluate an inspected plugin without mutating the workspace.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompatibilityContext {
    /// Running `SoftPilot` host version.
    pub host_version: Version,
    /// Exact plugin API version implemented by the host.
    pub plugin_api: Version,
    /// Native host target used for package selection.
    pub host_triple: HostTriple,
}

impl CompatibilityContext {
    /// Builds compatibility facts for the running binary.
    ///
    /// # Errors
    ///
    /// Returns [`CompatibilityError`] if compiled version constants are invalid or the current
    /// native host is outside the supported matrix.
    pub fn current() -> Result<Self, CompatibilityError> {
        Ok(Self {
            host_version: Version::parse(env!("CARGO_PKG_VERSION")).map_err(|source| {
                CompatibilityError::InvalidCompiledVersion {
                    field: "host",
                    value: env!("CARGO_PKG_VERSION"),
                    source,
                }
            })?,
            plugin_api: Version::parse(SUPPORTED_PLUGIN_API).map_err(|source| {
                CompatibilityError::InvalidCompiledVersion {
                    field: "plugin API",
                    value: SUPPORTED_PLUGIN_API,
                    source,
                }
            })?,
            host_triple: HostTriple::detect()?,
        })
    }

    /// Creates an explicit context for embedding and deterministic tests.
    #[must_use]
    pub const fn new(host_version: Version, plugin_api: Version, host_triple: HostTriple) -> Self {
        Self {
            host_version,
            plugin_api,
            host_triple,
        }
    }
}

impl PluginManifest {
    /// Ensures this manifest can run on the supplied host and target.
    ///
    /// # Errors
    ///
    /// Returns [`CompatibilityError`] for an invalid host requirement, plugin API mismatch, host
    /// version mismatch, or unsupported target.
    pub fn ensure_compatible(
        &self,
        context: &CompatibilityContext,
    ) -> Result<(), CompatibilityError> {
        if self.plugin_api != context.plugin_api {
            return Err(CompatibilityError::PluginApiMismatch {
                required: self.plugin_api.clone(),
                supported: context.plugin_api.clone(),
            });
        }

        if let Some(requirement) = &self.host_version {
            let parsed = VersionReq::parse(requirement).map_err(|source| {
                CompatibilityError::InvalidHostRequirement {
                    value: requirement.clone(),
                    source,
                }
            })?;
            if !parsed.matches(&context.host_version) {
                return Err(CompatibilityError::HostVersionMismatch {
                    required: requirement.clone(),
                    actual: context.host_version.clone(),
                });
            }
        }

        let host_target = context.host_triple.platform_target();
        if !self
            .targets
            .iter()
            .any(|target| target.is_compatible_with(host_target))
        {
            return Err(CompatibilityError::UnsupportedTarget {
                host_triple: context.host_triple,
            });
        }

        Ok(())
    }
}

/// Plugin compatibility evaluation error.
#[derive(Debug, Error)]
pub enum CompatibilityError {
    /// The running platform is outside the supported host matrix.
    #[error(transparent)]
    Host(#[from] HostTripleError),
    /// A version constant embedded in this build is invalid.
    #[error("compiled {field} version is invalid ({value}): {source}")]
    InvalidCompiledVersion {
        /// Constant being parsed.
        field: &'static str,
        /// Embedded value.
        value: &'static str,
        /// Semantic version parser error.
        #[source]
        source: semver::Error,
    },
    /// An unvalidated manifest contained an invalid host requirement.
    #[error("invalid host version requirement {value}: {source}")]
    InvalidHostRequirement {
        /// Requirement supplied by the manifest.
        value: String,
        /// Semantic version requirement parser error.
        #[source]
        source: semver::Error,
    },
    /// The plugin API must match the host's exact current contract.
    #[error("plugin requires API {required}; this host implements {supported}")]
    PluginApiMismatch {
        /// Exact API version requested by the plugin.
        required: Version,
        /// Exact API version implemented by the host.
        supported: Version,
    },
    /// The running host version is outside the optional manifest range.
    #[error("plugin requires host {required}; current host is {actual}")]
    HostVersionMismatch {
        /// Requirement supplied by the manifest.
        required: String,
        /// Running host version.
        actual: Version,
    },
    /// None of the package targets match the native host.
    #[error("plugin package has no target compatible with {host_triple}")]
    UnsupportedTarget {
        /// Native target being evaluated.
        host_triple: HostTriple,
    },
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::PluginManifest;

    fn manifest() -> PluginManifest {
        PluginManifest::from_slice(
            br#"{
              "schemaVersion": "0.1.0",
              "id": "dev.softpilot.compatibility",
              "version": "1.0.0",
              "pluginApi": "0.1.0",
              "hostVersion": ">=0.1.0, <0.2.0",
              "name": "Compatibility",
              "description": "Compatibility fixture",
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
            }"#,
        )
        .expect("valid compatibility manifest")
    }

    fn context() -> CompatibilityContext {
        CompatibilityContext::new(
            Version::new(0, 1, 0),
            Version::new(0, 1, 0),
            HostTriple::WindowsX86_64,
        )
    }

    #[test]
    fn accepts_matching_api_host_range_and_target() {
        manifest()
            .ensure_compatible(&context())
            .expect("compatible manifest");
    }

    #[test]
    fn reports_api_host_and_target_mismatches_separately() {
        let mut api = context();
        api.plugin_api = Version::new(0, 2, 0);
        assert!(matches!(
            manifest().ensure_compatible(&api),
            Err(CompatibilityError::PluginApiMismatch { .. })
        ));

        let mut host = context();
        host.host_version = Version::new(0, 2, 0);
        assert!(matches!(
            manifest().ensure_compatible(&host),
            Err(CompatibilityError::HostVersionMismatch { .. })
        ));

        let mut target = context();
        target.host_triple = HostTriple::LinuxX86_64Glibc;
        assert!(matches!(
            manifest().ensure_compatible(&target),
            Err(CompatibilityError::UnsupportedTarget { .. })
        ));
    }
}
