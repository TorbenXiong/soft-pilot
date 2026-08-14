use std::collections::BTreeSet;

use serde::Serialize;

use crate::{OperatingSystemPermission, PluginPermissions, ProcessPermission, ShellPermission};

/// One normalized permission identity used for grants and upgrade comparisons.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize)]
#[serde(tag = "kind", content = "value", rename_all = "kebab-case")]
pub enum PluginPermissionGrant {
    /// Access one canonical catalog HTTPS origin.
    CatalogOrigin(String),
    /// Access one canonical artifact HTTPS origin.
    ArtifactOrigin(String),
    /// Execute a host-controlled process scope.
    Process(ProcessPermission),
    /// Contribute one shell integration capability.
    Shell(ShellPermission),
    /// Request one operating-system integration capability.
    OperatingSystem(OperatingSystemPermission),
}

/// Stable added and removed permissions between two plugin versions.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PluginPermissionsDiff {
    /// Grants requested by the new version but absent from the previous version.
    pub added: Vec<PluginPermissionGrant>,
    /// Grants no longer requested by the new version.
    pub removed: Vec<PluginPermissionGrant>,
}

impl PluginPermissionsDiff {
    /// Compares an optional installed permission set with a newly requested set.
    #[must_use]
    pub fn between(previous: Option<&PluginPermissions>, requested: &PluginPermissions) -> Self {
        let previous = previous.map_or_else(BTreeSet::new, permission_set);
        let requested = permission_set(requested);
        Self {
            added: requested.difference(&previous).cloned().collect(),
            removed: previous.difference(&requested).cloned().collect(),
        }
    }

    /// Returns whether an install or upgrade expands authority and requires confirmation.
    #[must_use]
    pub fn requires_confirmation(&self) -> bool {
        !self.added.is_empty()
    }

    /// Returns whether both versions request exactly the same authority.
    #[must_use]
    pub fn is_unchanged(&self) -> bool {
        self.added.is_empty() && self.removed.is_empty()
    }
}

fn permission_set(permissions: &PluginPermissions) -> BTreeSet<PluginPermissionGrant> {
    permissions
        .network
        .catalog_origins
        .iter()
        .cloned()
        .map(PluginPermissionGrant::CatalogOrigin)
        .chain(
            permissions
                .network
                .artifact_origins
                .iter()
                .cloned()
                .map(PluginPermissionGrant::ArtifactOrigin),
        )
        .chain(
            permissions
                .process
                .iter()
                .copied()
                .map(PluginPermissionGrant::Process),
        )
        .chain(
            permissions
                .shell
                .iter()
                .copied()
                .map(PluginPermissionGrant::Shell),
        )
        .chain(
            permissions
                .os
                .iter()
                .copied()
                .map(PluginPermissionGrant::OperatingSystem),
        )
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::NetworkPermissions;

    #[test]
    fn reports_added_and_removed_grants_in_stable_order() {
        let previous = PluginPermissions {
            network: NetworkPermissions {
                catalog_origins: vec!["https://catalog.example".to_owned()],
                artifact_origins: Vec::new(),
            },
            process: vec![ProcessPermission::Staged],
            shell: vec![ShellPermission::Path],
            os: Vec::new(),
        };
        let requested = PluginPermissions {
            network: NetworkPermissions {
                catalog_origins: Vec::new(),
                artifact_origins: vec!["https://artifacts.example".to_owned()],
            },
            process: vec![ProcessPermission::Staged],
            shell: vec![ShellPermission::Shims],
            os: vec![OperatingSystemPermission::Shortcut],
        };

        let difference = PluginPermissionsDiff::between(Some(&previous), &requested);
        assert!(difference.requires_confirmation());
        assert_eq!(difference.added.len(), 3);
        assert_eq!(difference.removed.len(), 2);
        assert!(difference.added.windows(2).all(|pair| pair[0] < pair[1]));
        assert!(difference.removed.windows(2).all(|pair| pair[0] < pair[1]));
    }

    #[test]
    fn first_install_treats_every_grant_as_added() {
        let requested = PluginPermissions {
            network: NetworkPermissions {
                catalog_origins: Vec::new(),
                artifact_origins: Vec::new(),
            },
            process: vec![ProcessPermission::Installed],
            shell: Vec::new(),
            os: Vec::new(),
        };

        let difference = PluginPermissionsDiff::between(None, &requested);
        assert_eq!(
            difference.added,
            vec![PluginPermissionGrant::Process(ProcessPermission::Installed)]
        );
        assert!(difference.removed.is_empty());
    }
}
