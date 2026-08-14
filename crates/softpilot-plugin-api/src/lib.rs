//! Plugin manifest, package and WebAssembly component validation.

mod compatibility;
#[cfg(feature = "component-validation")]
mod component;
mod manifest;
mod package;
mod permissions;

pub use compatibility::{CompatibilityContext, CompatibilityError};
#[cfg(feature = "component-validation")]
pub use component::{ComponentError, PluginComponentDescriptor, inspect_component_descriptor};
pub use manifest::{
    ManagementLevel, ManifestError, NetworkPermissions, OperatingSystemPermission, PackageEntry,
    PluginAssets, PluginEntry, PluginKind, PluginManifest, PluginPermissions, ProcessPermission,
    Publisher, ShellPermission,
};
pub use package::{InspectedPlugin, PackageError, PluginPackageInspector};
pub use permissions::{PluginPermissionGrant, PluginPermissionsDiff};

/// Bindings generated from the versioned `SoftPilot` plugin WIT contract.
#[cfg(feature = "component-validation")]
pub mod wit {
    wasmtime::component::bindgen!({
        path: "../../specs/plugin/wit",
        world: "software-plugin",
    });
}
