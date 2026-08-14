//! Deterministic Component fixture implementing the SoftPilot lifecycle WIT world.

#![cfg(target_arch = "wasm32")]
#![cfg_attr(not(feature = "wasi-imports"), no_std)]

extern crate alloc;

use alloc::{
    string::{String, ToString},
    vec,
    vec::Vec,
};
#[cfg(not(feature = "wasi-imports"))]
use core::{
    alloc::{GlobalAlloc, Layout},
    ptr,
};

#[cfg(not(feature = "wasi-imports"))]
struct PageAllocator;

// The fixture is short-lived and only exercises the host ABI. Each allocation receives fresh
// linear-memory pages so the Component remains capability-free without pulling in a WASI libc.
#[cfg(not(feature = "wasi-imports"))]
unsafe impl GlobalAlloc for PageAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        const PAGE_SIZE: usize = 65_536;

        let required = layout
            .size()
            .saturating_add(layout.align().saturating_sub(1));
        let pages = required.div_ceil(PAGE_SIZE).max(1);
        let previous_pages = core::arch::wasm32::memory_grow::<0>(pages);
        if previous_pages == usize::MAX {
            return ptr::null_mut();
        }

        let base = previous_pages.saturating_mul(PAGE_SIZE);
        let aligned = base.saturating_add(layout.align() - 1) & !(layout.align() - 1);
        aligned as *mut u8
    }

    unsafe fn dealloc(&self, _pointer: *mut u8, _layout: Layout) {}
}

#[cfg(not(feature = "wasi-imports"))]
#[global_allocator]
static ALLOCATOR: PageAllocator = PageAllocator;

#[cfg(not(feature = "wasi-imports"))]
#[unsafe(export_name = "cabi_realloc")]
unsafe extern "C" fn canonical_abi_realloc(
    old_pointer: *mut u8,
    old_size: usize,
    alignment: usize,
    new_size: usize,
) -> *mut u8 {
    if new_size == 0 {
        return alignment as *mut u8;
    }

    let layout = unsafe { Layout::from_size_align_unchecked(new_size, alignment) };
    let new_pointer = unsafe { ALLOCATOR.alloc(layout) };
    if !old_pointer.is_null() && !new_pointer.is_null() {
        unsafe { ptr::copy_nonoverlapping(old_pointer, new_pointer, old_size.min(new_size)) };
    }
    new_pointer
}

#[cfg(not(feature = "wasi-imports"))]
#[panic_handler]
fn panic(_info: &core::panic::PanicInfo<'_>) -> ! {
    core::arch::wasm32::unreachable()
}

mod bindings {
    wit_bindgen::generate!({
        path: "../../specs/plugin/wit",
        world: "software-plugin",
    });
}

use bindings::exports::softpilot::plugin::lifecycle::{
    Activation, CatalogRequest, CatalogResponse, ErrorCode, Guest, HealthProbe, HealthResult,
    HostContext, InstallPlan, InstallRequest, PluginDescriptor, PluginError, ProbeResult, Release,
};

struct Fixture;

#[cfg(feature = "wasi-imports")]
fn exercise_wasi_import() {
    let _environment = std::env::vars_os().count();
}

#[cfg(feature = "trap")]
fn exercise_trap() {
    panic!("intentional fixture trap");
}

#[cfg(feature = "non-terminating")]
fn exercise_non_termination() {
    loop {
        core::hint::spin_loop();
    }
}

#[cfg(feature = "excessive-memory")]
fn exercise_excessive_memory() {
    let allocation = vec![0_u8; 16 * 1024 * 1024];
    core::hint::black_box(&allocation);
}

impl Guest for Fixture {
    fn descriptor() -> PluginDescriptor {
        #[cfg(feature = "wasi-imports")]
        exercise_wasi_import();

        #[cfg(feature = "trap")]
        exercise_trap();

        #[cfg(feature = "non-terminating")]
        exercise_non_termination();

        #[cfg(feature = "excessive-memory")]
        exercise_excessive_memory();

        PluginDescriptor {
            id: "dev.softpilot.lifecycle-fixture".into(),
            version: "0.1.0".into(),
            plugin_api: "0.1.0".into(),
        }
    }

    fn catalog_requests(_host: HostContext) -> Result<Vec<CatalogRequest>, PluginError> {
        Ok(Vec::new())
    }

    fn parse_catalog(
        _host: HostContext,
        _responses: Vec<CatalogResponse>,
    ) -> Result<Vec<Release>, PluginError> {
        Ok(Vec::new())
    }

    fn plan_install(request: InstallRequest) -> Result<InstallPlan, PluginError> {
        Ok(InstallPlan {
            instance_directory_name: request.exact_version.clone(),
            exact_version: request.exact_version,
            steps: Vec::new(),
            health: HealthProbe {
                executable: "fixture".into(),
                arguments: vec!["--version".into()],
                environment: Vec::new(),
                maximum_output_bytes: 4096,
            },
            activation: Activation {
                commands: Vec::new(),
                path_entries: Vec::new(),
                environment: Vec::new(),
            },
        })
    }

    fn evaluate_health(
        exact_version: String,
        probe: ProbeResult,
    ) -> Result<HealthResult, PluginError> {
        let healthy = probe.exit_code == 0;
        Ok(HealthResult {
            healthy,
            actual_version: healthy.then_some(exact_version),
            message: (!healthy).then_some(probe.standard_error),
        })
    }

    fn config_schema() -> Option<String> {
        None
    }

    fn parse_config(content: Vec<u8>) -> Result<String, PluginError> {
        String::from_utf8(content).map_err(|error| PluginError {
            code: ErrorCode::InvalidConfig,
            message: error.to_string(),
        })
    }

    fn render_config(canonical_json: String) -> Result<Vec<u8>, PluginError> {
        Ok(canonical_json.into_bytes())
    }
}

bindings::export!(Fixture with_types_in bindings);
