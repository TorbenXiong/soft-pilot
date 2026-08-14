use thiserror::Error;
use wasmtime::{
    Config, Engine, Store, StoreLimits, StoreLimitsBuilder,
    component::{Component, Linker},
};

const LIFECYCLE_INTERFACE: &str = "softpilot:plugin/lifecycle@0.1.0";
const DESCRIPTOR_FUEL: u64 = 1_000_000;
const COMPONENT_MEMORY_LIMIT: usize = 8 * 1024 * 1024;

/// Validates that bytes contain a WebAssembly Component accepted by the host engine.
pub(crate) fn validate_component(bytes: &[u8]) -> Result<(), ComponentError> {
    let (engine, component) = compile_component(bytes)?;
    validate_contract(&engine, &component)
}

/// Validates and instantiates a plugin Component, then reads its lifecycle descriptor.
///
/// # Errors
///
/// Returns [`ComponentError`] when the bytes are not a valid capability-free lifecycle
/// Component, instantiation fails, or the descriptor call traps.
pub fn inspect_component_descriptor(
    bytes: &[u8],
) -> Result<PluginComponentDescriptor, ComponentError> {
    let (engine, component) = compile_component(bytes)?;
    validate_contract(&engine, &component)?;

    let linker = Linker::<ComponentStore>::new(&engine);
    let mut store = Store::new(&engine, ComponentStore::new());
    store.limiter(|state| &mut state.limits);
    store.set_fuel(DESCRIPTOR_FUEL)?;
    let plugin = crate::wit::SoftwarePlugin::instantiate(&mut store, &component, &linker)?;
    let descriptor = plugin
        .softpilot_plugin_lifecycle()
        .call_descriptor(&mut store)?;

    Ok(PluginComponentDescriptor {
        id: descriptor.id,
        version: descriptor.version,
        plugin_api: descriptor.plugin_api,
    })
}

fn compile_component(bytes: &[u8]) -> Result<(Engine, Component), ComponentError> {
    let mut config = Config::new();
    config.wasm_component_model(true);
    config.consume_fuel(true);
    let engine = Engine::new(&config)?;
    let component = Component::new(&engine, bytes)?;
    Ok((engine, component))
}

fn validate_contract(engine: &Engine, component: &Component) -> Result<(), ComponentError> {
    let component_type = component.component_type();

    let imports: Vec<_> = component_type
        .imports(engine)
        .map(|(name, _)| name.to_owned())
        .collect();
    if !imports.is_empty() {
        return Err(ComponentError::UnexpectedImports(imports.join(", ")));
    }

    let exports_lifecycle = component_type.exports(engine).any(|(name, export)| {
        name == LIFECYCLE_INTERFACE || export.is_implements(LIFECYCLE_INTERFACE)
    });
    if !exports_lifecycle {
        return Err(ComponentError::MissingLifecycleExport);
    }

    Ok(())
}

struct ComponentStore {
    limits: StoreLimits,
}

impl ComponentStore {
    fn new() -> Self {
        Self {
            limits: StoreLimitsBuilder::new()
                .memory_size(COMPONENT_MEMORY_LIMIT)
                .instances(16)
                .memories(4)
                .tables(4)
                .trap_on_grow_failure(true)
                .build(),
        }
    }
}

/// Identity returned by a plugin Component's lifecycle interface.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PluginComponentDescriptor {
    /// Stable reverse-DNS plugin identifier.
    pub id: String,
    /// Exact plugin implementation version.
    pub version: String,
    /// Plugin API version implemented by the Component.
    pub plugin_api: String,
}

/// Plugin Component validation or invocation failure.
#[derive(Debug, Error)]
pub enum ComponentError {
    #[error("invalid WebAssembly component: {0}")]
    Wasmtime(#[from] wasmtime::Error),
    #[error("component must not import host capabilities; found: {0}")]
    UnexpectedImports(String),
    #[error("component does not implement {LIFECYCLE_INTERFACE}")]
    MissingLifecycleExport,
}

#[cfg(test)]
mod tests {
    use std::{env, fs};

    use super::*;

    #[test]
    fn rejects_malformed_component_bytes() {
        assert!(matches!(
            validate_component(b"not a component"),
            Err(ComponentError::Wasmtime(_))
        ));
    }

    #[test]
    fn rejects_valid_component_without_plugin_contract() {
        let empty_component = b"\0asm\x0d\0\x01\0";
        assert!(matches!(
            validate_component(empty_component),
            Err(ComponentError::MissingLifecycleExport)
        ));
    }

    #[test]
    #[ignore = "requires the separately built lifecycle Component fixture"]
    fn loads_fixture_descriptor() {
        let path = env::var_os("SOFTPILOT_TEST_COMPONENT")
            .expect("SOFTPILOT_TEST_COMPONENT must point to the built fixture");
        let bytes = fs::read(path).expect("read lifecycle Component fixture");

        let descriptor = inspect_component_descriptor(&bytes).expect("load fixture descriptor");

        assert_eq!(descriptor.id, "dev.softpilot.lifecycle-fixture");
        assert_eq!(descriptor.version, "0.1.0");
        assert_eq!(descriptor.plugin_api, "0.1.0");
    }

    #[test]
    #[ignore = "requires the separately built lifecycle Component fixture"]
    fn rejects_fixture_with_wasi_imports() {
        let bytes = fixture_bytes();

        let error = inspect_component_descriptor(&bytes).expect_err("reject WASI imports");

        assert!(matches!(error, ComponentError::UnexpectedImports(_)));
    }

    #[test]
    #[ignore = "requires the separately built lifecycle Component fixture"]
    fn isolates_fixture_trap() {
        assert_runtime_failure("unreachable");
    }

    #[test]
    #[ignore = "requires the separately built lifecycle Component fixture"]
    fn interrupts_non_terminating_fixture() {
        assert_runtime_failure("fuel");
    }

    #[test]
    #[ignore = "requires the separately built lifecycle Component fixture"]
    fn rejects_fixture_exceeding_memory_limit() {
        assert_runtime_failure("forcing trap when growing memory");
    }

    fn fixture_bytes() -> Vec<u8> {
        let path = env::var_os("SOFTPILOT_TEST_COMPONENT")
            .expect("SOFTPILOT_TEST_COMPONENT must point to the built fixture");
        fs::read(path).expect("read lifecycle Component fixture")
    }

    fn assert_runtime_failure(expected_message: &str) {
        let bytes = fixture_bytes();
        let error = inspect_component_descriptor(&bytes).expect_err("reject malicious fixture");
        let ComponentError::Wasmtime(error) = error else {
            panic!("expected runtime failure, got {error}");
        };
        let message = format!("{error:#}");
        assert!(
            message.contains(expected_message),
            "runtime failure did not contain {expected_message:?}: {message}"
        );
    }
}
