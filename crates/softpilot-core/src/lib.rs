//! Platform-neutral domain types shared by the `SoftPilot` host and plugin tooling.

mod host;
mod workspace;

use std::{fmt, str::FromStr};

use serde::{Deserialize, Deserializer, Serialize, Serializer, de};
use thiserror::Error;

pub use host::{HostTriple, HostTripleError};
pub use workspace::{
    WorkspaceId, WorkspaceLayoutVersion, WorkspaceLayoutVersionError, WorkspaceMetadata,
    WorkspacePath, WorkspacePathError,
};

/// A validated, globally unique plugin identifier.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct PluginId(String);

impl PluginId {
    /// Returns the validated identifier.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for PluginId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl FromStr for PluginId {
    type Err = PluginIdError;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        if value.len() > 160 {
            return Err(PluginIdError::TooLong);
        }

        let mut has_separator = false;
        let mut previous_was_separator = false;
        for (index, character) in value.char_indices() {
            let is_separator = matches!(character, '.' | '-');
            if is_separator {
                has_separator = true;
                if index == 0 || previous_was_separator {
                    return Err(PluginIdError::InvalidFormat);
                }
            } else if !character.is_ascii_lowercase() && !character.is_ascii_digit() {
                return Err(PluginIdError::InvalidFormat);
            }
            previous_was_separator = is_separator;
        }

        if value.is_empty() || !has_separator || previous_was_separator {
            return Err(PluginIdError::InvalidFormat);
        }

        Ok(Self(value.to_owned()))
    }
}

impl Serialize for PluginId {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_str(self.as_str())
    }
}

impl<'de> Deserialize<'de> for PluginId {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let value = String::deserialize(deserializer)?;
        value.parse().map_err(de::Error::custom)
    }
}

/// Validation error for a plugin identifier.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum PluginIdError {
    /// The identifier exceeds the manifest limit.
    #[error("plugin id exceeds 160 bytes")]
    TooLong,
    /// The identifier does not follow the reverse-domain-style format.
    #[error("plugin id must contain lowercase alphanumeric segments separated by '.' or '-'")]
    InvalidFormat,
}

/// Operating systems supported by a plugin target.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum OperatingSystem {
    /// Microsoft Windows.
    Windows,
    /// Apple macOS.
    #[serde(rename = "macos")]
    MacOs,
    /// Linux.
    Linux,
}

/// CPU architectures supported by a plugin target.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Architecture {
    /// 64-bit x86.
    #[serde(rename = "x86-64")]
    X86_64,
    /// 64-bit Arm.
    Arm64,
}

/// C runtime constraint for a plugin target.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Libc {
    /// GNU libc.
    Glibc,
}

/// A host or plugin platform target.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlatformTarget {
    /// Target operating system.
    pub os: OperatingSystem,
    /// Target CPU architecture.
    pub architecture: Architecture,
    /// Optional C runtime restriction, valid only on Linux.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub libc: Option<Libc>,
}

impl PlatformTarget {
    /// Validates target-specific constraints.
    ///
    /// # Errors
    ///
    /// Returns [`PlatformTargetError`] when a libc constraint is used outside Linux.
    pub fn validate(self) -> Result<Self, PlatformTargetError> {
        if self.os != OperatingSystem::Linux && self.libc.is_some() {
            return Err(PlatformTargetError::LibcOnNonLinux);
        }
        Ok(self)
    }

    /// Returns whether this target is compatible with the supplied host.
    #[must_use]
    pub fn is_compatible_with(self, host: Self) -> bool {
        self.os == host.os
            && self.architecture == host.architecture
            && self.libc.is_none_or(|required| host.libc == Some(required))
    }
}

/// Validation error for a platform target.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum PlatformTargetError {
    /// A libc was declared for Windows or macOS.
    #[error("libc may only be specified for Linux targets")]
    LibcOnNonLinux,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plugin_id_accepts_reverse_domain_style_value() {
        let id: PluginId = "dev.softpilot.node-js".parse().expect("valid plugin id");
        assert_eq!(id.as_str(), "dev.softpilot.node-js");
    }

    #[test]
    fn plugin_id_rejects_missing_or_repeated_separator() {
        for value in ["node", ".node", "node.", "dev..node", "Dev.node"] {
            assert!(value.parse::<PluginId>().is_err(), "accepted {value}");
        }
    }

    #[test]
    fn non_linux_target_rejects_libc() {
        let target = PlatformTarget {
            os: OperatingSystem::Windows,
            architecture: Architecture::X86_64,
            libc: Some(Libc::Glibc),
        };
        assert_eq!(target.validate(), Err(PlatformTargetError::LibcOnNonLinux));
    }

    #[test]
    fn target_compatibility_honors_optional_libc_constraint() {
        let host = PlatformTarget {
            os: OperatingSystem::Linux,
            architecture: Architecture::X86_64,
            libc: Some(Libc::Glibc),
        };
        let target = PlatformTarget { libc: None, ..host };
        assert!(target.is_compatible_with(host));
    }
}
