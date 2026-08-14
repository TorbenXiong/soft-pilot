//! Plugin manifest, package and WebAssembly component validation.

#[cfg(feature = "component-validation")]
mod component;
mod manifest;
mod package;

#[cfg(feature = "component-validation")]
pub use component::{ComponentError, PluginComponentDescriptor, inspect_component_descriptor};
pub use manifest::{
    ManagementLevel, ManifestError, NetworkPermissions, OperatingSystemPermission, PackageEntry,
    PluginAssets, PluginEntry, PluginKind, PluginManifest, PluginPermissions, ProcessPermission,
    Publisher, ShellPermission,
};
pub use package::{InspectedPlugin, PackageError, PluginPackageInspector};

/// Bindings generated from the versioned `SoftPilot` plugin WIT contract.
#[cfg(feature = "component-validation")]
pub mod wit {
    wasmtime::component::bindgen!({
        path: "../../specs/plugin/wit",
        world: "software-plugin",
    });
}
