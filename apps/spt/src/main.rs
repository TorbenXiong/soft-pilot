use std::{
    error::Error,
    fmt,
    fs::OpenOptions,
    io::Write,
    path::{Path, PathBuf},
    process::ExitCode,
    thread,
    time::Duration,
};

use clap::{Parser, Subcommand};
use softpilot_engine::{
    PluginActivationResult, PluginInstallError, PluginInstallResult, PluginPackageStatus,
    PluginRecoveryResult, PluginService, PluginTrashResult, PluginTrashStatus, WorkspaceError,
    WorkspaceInfo, WorkspaceInitResult, WorkspaceService,
};
use softpilot_plugin_api::PluginPackageInspector;

#[derive(Debug, Parser)]
#[command(name = "spt", version, about = "SoftPilot cross-platform plugin host")]
struct Cli {
    /// Override workspace discovery for this invocation.
    #[arg(long, global = true, value_name = "PATH")]
    workspace: Option<PathBuf>,
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Inspect and validate plugin packages.
    Plugin {
        #[command(subcommand)]
        command: PluginCommand,
    },
    /// Initialize or inspect the selected workspace.
    Workspace {
        #[command(subcommand)]
        command: WorkspaceCommand,
    },
}

#[derive(Debug, Subcommand)]
enum PluginCommand {
    /// Validate a local .softpilot-plugin package without installing it.
    Inspect {
        /// Path to the plugin package.
        package: PathBuf,
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Install a validated package into the selected workspace.
    Install {
        /// Path to the plugin package.
        package: PathBuf,
        /// Explicitly accept permissions added by this version.
        #[arg(long)]
        accept_permissions: bool,
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// List every installed plugin version and its active state.
    List {
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Enable an installed plugin version.
    Enable {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact installed version; defaults to the highest semantic version.
        #[arg(long)]
        version: Option<String>,
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Disable a plugin without deleting installed versions.
    Disable {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Move an inactive installed version into recoverable trash.
    Uninstall {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact installed version to move to trash.
        #[arg(long)]
        version: String,
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// List recoverable plugin package trash entries.
    Trash {
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Restore an exact plugin version from recoverable trash.
    Restore {
        /// Stable plugin identifier.
        plugin_id: String,
        /// Exact trashed version to restore.
        #[arg(long)]
        version: String,
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Reconcile interrupted plugin filesystem operations from the durable journal.
    Recover {
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
}

#[derive(Debug, Subcommand)]
enum WorkspaceCommand {
    /// Initialize the directory supplied by --workspace and remember it for later invocations.
    Init {
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Show the first valid workspace from the documented discovery order.
    Show {
        /// Emit stable machine-readable JSON.
        #[arg(long)]
        json: bool,
    },
    /// Internal cross-process lock probe used by the native test matrix.
    #[command(hide = true)]
    LockProbe {
        /// Diagnostic operation name.
        #[arg(long)]
        operation: String,
        /// Maximum time to wait for the workspace lock.
        #[arg(long)]
        timeout_ms: u64,
        /// Time to retain the lock after acquisition.
        #[arg(long, default_value_t = 0)]
        hold_ms: u64,
        /// Create this file after acquiring the lock.
        #[arg(long)]
        ready_file: Option<PathBuf>,
    },
}

impl Cli {
    fn json_output(&self) -> bool {
        match &self.command {
            Command::Plugin { command } => command.json_output(),
            Command::Workspace { command } => command.json_output(),
        }
    }
}

impl PluginCommand {
    const fn json_output(&self) -> bool {
        match self {
            Self::Inspect { json, .. }
            | Self::Install { json, .. }
            | Self::List { json }
            | Self::Enable { json, .. }
            | Self::Disable { json, .. }
            | Self::Uninstall { json, .. }
            | Self::Trash { json }
            | Self::Restore { json, .. }
            | Self::Recover { json } => *json,
        }
    }
}

impl WorkspaceCommand {
    const fn json_output(&self) -> bool {
        match self {
            Self::Init { json } | Self::Show { json } => *json,
            Self::LockProbe { .. } => false,
        }
    }
}

type CliResult<T> = Result<T, CliFailure>;

#[derive(Debug)]
struct CliFailure {
    code: &'static str,
    stage: &'static str,
    exit_code: u8,
    source: Box<dyn Error>,
}

impl CliFailure {
    fn new(
        code: &'static str,
        stage: &'static str,
        exit_code: u8,
        source: impl Error + 'static,
    ) -> Self {
        Self {
            code,
            stage,
            exit_code,
            source: Box::new(source),
        }
    }

    fn message(
        code: &'static str,
        stage: &'static str,
        exit_code: u8,
        message: &'static str,
    ) -> Self {
        Self::new(code, stage, exit_code, CliMessage(message))
    }
}

impl fmt::Display for CliFailure {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{} [{}]: {}", self.stage, self.code, self.source)
    }
}

impl Error for CliFailure {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        Some(self.source.as_ref())
    }
}

#[derive(Debug)]
struct CliMessage(&'static str);

impl fmt::Display for CliMessage {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.0)
    }
}

impl Error for CliMessage {}

macro_rules! print_json {
    ($data:expr) => {{
        let data = serde_json::to_value($data).map_err(output_failure)?;
        print_json_value(&data)
    }};
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let json = cli.json_output();
    match run(cli) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            if json {
                match failure_json(&error) {
                    Ok(serialized) => eprintln!("{serialized}"),
                    Err(serialization) => {
                        eprintln!("error: failed to serialize error: {serialization}");
                    }
                }
            } else {
                eprintln!("error: {error}");
            }
            ExitCode::from(error.exit_code)
        }
    }
}

fn run(cli: Cli) -> CliResult<()> {
    let Cli { workspace, command } = cli;
    match command {
        Command::Plugin { command } => run_plugin(command, workspace.as_deref())?,
        Command::Workspace { command } => run_workspace(command, workspace)?,
    }
    Ok(())
}

fn run_plugin(command: PluginCommand, workspace_override: Option<&Path>) -> CliResult<()> {
    match command {
        PluginCommand::Inspect { package, json } => {
            let inspected = PluginPackageInspector::inspect(package)
                .map_err(|error| CliFailure::new("package-invalid", "plugin.inspect", 20, error))?;
            if json {
                print_json!(&inspected)?;
            } else {
                println!(
                    "Plugin: {} ({})",
                    inspected.manifest.name, inspected.manifest.id
                );
                println!("Version: {}", inspected.manifest.version);
                println!("Publisher: {}", inspected.manifest.publisher.name);
                println!("Package bytes: {}", inspected.package_size_bytes);
                println!("SHA-256: {}", inspected.package_sha256);
                println!("Package entries: {}", inspected.entries.len());
                println!(
                    "Wasm component validated: {}",
                    inspected.component_validated
                );
            }
        }
        PluginCommand::Install {
            package,
            accept_permissions,
            json,
        } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let installed = PluginService::new(service)
                .install(&workspace, package, accept_permissions)
                .map_err(|error| plugin_failure("plugin.install", error))?;
            print_plugin_install(&installed, json)?;
        }
        PluginCommand::List { json } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let installed = PluginService::new(service)
                .list(&workspace)
                .map_err(|error| plugin_failure("plugin.list", error))?;
            print_plugin_list(&installed, json)?;
        }
        PluginCommand::Enable {
            plugin_id,
            version,
            json,
        } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let activated = PluginService::new(service)
                .enable(&workspace, &plugin_id, version.as_deref())
                .map_err(|error| plugin_failure("plugin.enable", error))?;
            print_plugin_activation(&activated, json)?;
        }
        PluginCommand::Disable { plugin_id, json } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let disabled = PluginService::new(service)
                .disable(&workspace, &plugin_id)
                .map_err(|error| plugin_failure("plugin.disable", error))?;
            print_plugin_activation(&disabled, json)?;
        }
        PluginCommand::Uninstall {
            plugin_id,
            version,
            json,
        } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let trashed = PluginService::new(service)
                .uninstall(&workspace, &plugin_id, &version)
                .map_err(|error| plugin_failure("plugin.uninstall", error))?;
            print_plugin_trash_result(&trashed, json, "moved to trash", "already in trash")?;
        }
        PluginCommand::Trash { json } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let trashed = PluginService::new(service)
                .trash(&workspace)
                .map_err(|error| plugin_failure("plugin.trash", error))?;
            print_plugin_trash(&trashed, json)?;
        }
        PluginCommand::Restore {
            plugin_id,
            version,
            json,
        } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let restored = PluginService::new(service)
                .restore(&workspace, &plugin_id, &version)
                .map_err(|error| plugin_failure("plugin.restore", error))?;
            print_plugin_trash_result(&restored, json, "restored", "already restored")?;
        }
        PluginCommand::Recover { json } => {
            let (service, workspace) = resolve_workspace(workspace_override)?;
            let recovered = PluginService::new(service)
                .recover(&workspace)
                .map_err(|error| plugin_failure("plugin.recover", error))?;
            print_plugin_recovery(&recovered, json)?;
        }
    }
    Ok(())
}

fn run_workspace(command: WorkspaceCommand, workspace_override: Option<PathBuf>) -> CliResult<()> {
    match command {
        WorkspaceCommand::Init { json } => {
            let path = workspace_override.ok_or_else(|| {
                CliFailure::message(
                    "workspace-required",
                    "workspace.init",
                    10,
                    "workspace init requires --workspace <PATH>",
                )
            })?;
            let service = WorkspaceService::for_current_process()
                .map_err(|error| workspace_failure("workspace.init", error))?;
            let initialized = service
                .initialize(&path)
                .map_err(|error| workspace_failure("workspace.init", error))?;
            service
                .remember(&initialized.workspace.path)
                .map_err(|error| workspace_failure("workspace.remember", error))?;
            print_workspace_init(&initialized, json)?;
        }
        WorkspaceCommand::Show { json } => {
            let service = WorkspaceService::for_current_process()
                .map_err(|error| workspace_failure("workspace.show", error))?;
            let workspace = service
                .resolve(workspace_override.as_deref())
                .map_err(|error| workspace_failure("workspace.resolve", error))?
                .ok_or_else(workspace_missing)?;
            print_workspace(&workspace, json)?;
        }
        WorkspaceCommand::LockProbe {
            operation,
            timeout_ms,
            hold_ms,
            ready_file,
        } => {
            let service = WorkspaceService::for_current_process()
                .map_err(|error| workspace_failure("workspace.lock-probe", error))?;
            let workspace = service
                .resolve(workspace_override.as_deref())
                .map_err(|error| workspace_failure("workspace.lock-probe", error))?
                .ok_or_else(|| {
                    CliFailure::message(
                        "workspace-required",
                        "workspace.lock-probe",
                        10,
                        "workspace lock probe requires a configured workspace",
                    )
                })?;
            let _guard = service
                .acquire_lock(
                    &workspace.path,
                    &operation,
                    Duration::from_millis(timeout_ms),
                )
                .map_err(|error| workspace_failure("workspace.lock-probe", error))?;
            if let Some(path) = ready_file {
                let mut file = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(path)
                    .map_err(|error| {
                        CliFailure::new("io-error", "workspace.lock-probe", 40, error)
                    })?;
                file.write_all(b"ready\n").map_err(|error| {
                    CliFailure::new("io-error", "workspace.lock-probe", 40, error)
                })?;
                file.sync_all().map_err(|error| {
                    CliFailure::new("io-error", "workspace.lock-probe", 40, error)
                })?;
            }
            thread::sleep(Duration::from_millis(hold_ms));
        }
    }
    Ok(())
}

fn resolve_workspace(
    workspace_override: Option<&Path>,
) -> CliResult<(WorkspaceService, WorkspaceInfo)> {
    let service = WorkspaceService::for_current_process()
        .map_err(|error| workspace_failure("workspace.resolve", error))?;
    let workspace = service
        .resolve(workspace_override)
        .map_err(|error| workspace_failure("workspace.resolve", error))?
        .ok_or_else(workspace_missing)?;
    Ok((service, workspace))
}

fn workspace_missing() -> CliFailure {
    CliFailure::message(
        "workspace-required",
        "workspace.resolve",
        10,
        "no workspace was configured; use --workspace <PATH> workspace init",
    )
}

fn workspace_failure(stage: &'static str, error: WorkspaceError) -> CliFailure {
    let (code, exit_code) = match &error {
        WorkspaceError::LockTimeout { .. } => ("workspace-locked", 30),
        WorkspaceError::LayoutVersion(_) | WorkspaceError::UnsupportedPointerVersion { .. } => {
            ("workspace-incompatible", 11)
        }
        WorkspaceError::DirectoryNotEmpty(_) => ("workspace-not-empty", 12),
        _ => ("workspace-error", 10),
    };
    CliFailure::new(code, stage, exit_code, error)
}

fn plugin_failure(stage: &'static str, error: PluginInstallError) -> CliFailure {
    let (code, exit_code) = match &error {
        PluginInstallError::Workspace(WorkspaceError::LockTimeout { .. }) => {
            ("workspace-locked", 30)
        }
        PluginInstallError::Workspace(_) => ("workspace-error", 10),
        PluginInstallError::PluginId(_)
        | PluginInstallError::Package(_)
        | PluginInstallError::Manifest(_)
        | PluginInstallError::StagedPackageChanged { .. }
        | PluginInstallError::StagedValidationChanged
        | PluginInstallError::CommittedPackageInvalid { .. }
        | PluginInstallError::CommittedPackageChanged(_) => ("package-invalid", 20),
        PluginInstallError::Compatibility(_) => ("plugin-incompatible", 21),
        PluginInstallError::PermissionConfirmationRequired { .. } => {
            ("permission-confirmation-required", 22)
        }
        PluginInstallError::PluginNotInstalled(_)
        | PluginInstallError::PluginVersionNotInstalled { .. }
        | PluginInstallError::ActivePluginCannotUninstall { .. }
        | PluginInstallError::TrashedPluginVersionNotFound { .. }
        | PluginInstallError::VersionDigestConflict { .. }
        | PluginInstallError::UntrackedVersionDirectory(_) => ("plugin-state-conflict", 23),
        PluginInstallError::InvalidRecoveryJournal(_)
        | PluginInstallError::AmbiguousRecoveryState { .. }
        | PluginInstallError::StoredPackageMetadataMismatch { .. }
        | PluginInstallError::StoredTrashMetadataMismatch { .. }
        | PluginInstallError::UnsafeDirectory(_) => ("plugin-state-unsafe", 24),
        _ => ("plugin-internal-error", 40),
    };
    CliFailure::new(code, stage, exit_code, error)
}

fn output_failure(error: serde_json::Error) -> CliFailure {
    CliFailure::new("output-serialization", "output.serialize", 50, error)
}

fn print_json_value(data: &serde_json::Value) -> CliResult<()> {
    let serialized = success_json(data).map_err(output_failure)?;
    println!("{serialized}");
    Ok(())
}

fn success_json(data: &serde_json::Value) -> Result<String, serde_json::Error> {
    serde_json::to_string_pretty(&serde_json::json!({ "ok": true, "data": data }))
}

fn failure_json(error: &CliFailure) -> Result<String, serde_json::Error> {
    serde_json::to_string_pretty(&serde_json::json!({
        "ok": false,
        "error": {
            "code": error.code,
            "stage": error.stage,
            "message": error.source.to_string(),
        }
    }))
}

fn print_plugin_install(installed: &PluginInstallResult, json: bool) -> CliResult<()> {
    if json {
        print_json!(installed)?;
    } else {
        println!(
            "Plugin {} {}: {}",
            installed.plugin_id,
            installed.version,
            if installed.installed {
                "installed"
            } else {
                "already installed"
            }
        );
        println!("Package: {}", installed.package_path.display());
        println!("Package bytes: {}", installed.package_size_bytes);
        println!("SHA-256: {}", installed.package_sha256);
        println!("Added permissions: {}", installed.permissions.added.len());
        println!(
            "Removed permissions: {}",
            installed.permissions.removed.len()
        );
    }
    Ok(())
}

fn print_plugin_list(installed: &[PluginPackageStatus], json: bool) -> CliResult<()> {
    if json {
        print_json!(installed)?;
    } else if installed.is_empty() {
        println!("No plugins installed.");
    } else {
        for plugin in installed {
            println!(
                "{} {} ({}) [{}]",
                plugin.plugin_id,
                plugin.version,
                plugin.name,
                if plugin.active { "active" } else { "inactive" }
            );
        }
    }
    Ok(())
}

fn print_plugin_activation(result: &PluginActivationResult, json: bool) -> CliResult<()> {
    if json {
        print_json!(result)?;
    } else if let Some(version) = &result.active_version {
        println!(
            "Plugin {} {} {}",
            result.plugin_id,
            version,
            if result.changed {
                "enabled"
            } else {
                "already enabled"
            }
        );
    } else {
        println!(
            "Plugin {} {}",
            result.plugin_id,
            if result.changed {
                "disabled"
            } else {
                "already disabled"
            }
        );
    }
    Ok(())
}

fn print_plugin_trash(trashed: &[PluginTrashStatus], json: bool) -> CliResult<()> {
    if json {
        print_json!(trashed)?;
    } else if trashed.is_empty() {
        println!("Plugin trash is empty.");
    } else {
        for plugin in trashed {
            println!(
                "{} {} ({}) [{}]",
                plugin.plugin_id, plugin.version, plugin.name, plugin.trash_id
            );
        }
    }
    Ok(())
}

fn print_plugin_trash_result(
    result: &PluginTrashResult,
    json: bool,
    changed_label: &str,
    unchanged_label: &str,
) -> CliResult<()> {
    if json {
        print_json!(result)?;
    } else {
        println!(
            "Plugin {} {}: {}\nPackage: {}",
            result.plugin_id,
            result.version,
            if result.changed {
                changed_label
            } else {
                unchanged_label
            },
            result.package_path.display()
        );
    }
    Ok(())
}

fn print_plugin_recovery(result: &PluginRecoveryResult, json: bool) -> CliResult<()> {
    if json {
        print_json!(result)?;
    } else {
        println!(
            "Plugin recovery: {} completed, {} cancelled",
            result.completed, result.cancelled
        );
    }
    Ok(())
}

fn print_workspace_init(initialized: &WorkspaceInitResult, json: bool) -> CliResult<()> {
    if json {
        print_json!(initialized)?;
    } else {
        println!(
            "Workspace {}: {}",
            if initialized.created {
                "initialized"
            } else {
                "already initialized"
            },
            initialized.workspace.path
        );
        print_workspace_details(&initialized.workspace);
    }
    Ok(())
}

fn print_workspace(workspace: &WorkspaceInfo, json: bool) -> CliResult<()> {
    if json {
        print_json!(workspace)?;
    } else {
        println!("Workspace: {}", workspace.path);
        print_workspace_details(workspace);
    }
    Ok(())
}

fn print_workspace_details(workspace: &WorkspaceInfo) {
    println!("ID: {}", workspace.metadata.workspace_id);
    println!("Layout version: {}", workspace.metadata.layout_version);
    println!(
        "Created at (Unix seconds): {}",
        workspace.metadata.created_at_unix_seconds
    );
    println!("Host triple: {}", workspace.host_triple);
    println!("Location source: {}", workspace.source.as_str());
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_workspace_commands_with_a_global_override() {
        let init = Cli::try_parse_from([
            "spt",
            "workspace",
            "init",
            "--workspace",
            "/tmp/softpilot",
            "--json",
        ])
        .expect("parse workspace init");
        assert_eq!(init.workspace, Some(PathBuf::from("/tmp/softpilot")));
        assert!(matches!(
            init.command,
            Command::Workspace {
                command: WorkspaceCommand::Init { json: true }
            }
        ));

        let show = Cli::try_parse_from(["spt", "workspace", "show"]).expect("parse workspace show");
        assert!(matches!(
            show.command,
            Command::Workspace {
                command: WorkspaceCommand::Show { json: false }
            }
        ));
    }

    #[test]
    fn parses_plugin_install_permission_confirmation() {
        let install = Cli::try_parse_from([
            "spt",
            "--workspace",
            "/tmp/softpilot",
            "plugin",
            "install",
            "fixture.softpilot-plugin",
            "--accept-permissions",
            "--json",
        ])
        .expect("parse plugin install");
        assert!(matches!(
            install.command,
            Command::Plugin {
                command: PluginCommand::Install {
                    accept_permissions: true,
                    json: true,
                    ..
                }
            }
        ));
    }

    #[test]
    fn parses_plugin_lifecycle_state_commands() {
        let list =
            Cli::try_parse_from(["spt", "plugin", "list", "--json"]).expect("parse plugin list");
        assert!(matches!(
            list.command,
            Command::Plugin {
                command: PluginCommand::List { json: true }
            }
        ));

        let enable = Cli::try_parse_from([
            "spt",
            "plugin",
            "enable",
            "dev.softpilot.fixture",
            "--version",
            "1.2.3",
        ])
        .expect("parse plugin enable");
        assert!(matches!(
            enable.command,
            Command::Plugin {
                command: PluginCommand::Enable {
                    plugin_id,
                    version: Some(version),
                    json: false,
                }
            } if plugin_id == "dev.softpilot.fixture" && version == "1.2.3"
        ));

        let disable = Cli::try_parse_from([
            "spt",
            "plugin",
            "disable",
            "dev.softpilot.fixture",
            "--json",
        ])
        .expect("parse plugin disable");
        assert!(matches!(
            disable.command,
            Command::Plugin {
                command: PluginCommand::Disable {
                    plugin_id,
                    json: true,
                }
            } if plugin_id == "dev.softpilot.fixture"
        ));

        let uninstall = Cli::try_parse_from([
            "spt",
            "plugin",
            "uninstall",
            "dev.softpilot.fixture",
            "--version",
            "1.2.3",
            "--json",
        ])
        .expect("parse plugin uninstall");
        assert!(matches!(
            uninstall.command,
            Command::Plugin {
                command: PluginCommand::Uninstall {
                    plugin_id,
                    version,
                    json: true,
                }
            } if plugin_id == "dev.softpilot.fixture" && version == "1.2.3"
        ));

        let trash =
            Cli::try_parse_from(["spt", "plugin", "trash", "--json"]).expect("parse trash list");
        assert!(matches!(
            trash.command,
            Command::Plugin {
                command: PluginCommand::Trash { json: true }
            }
        ));

        let restore = Cli::try_parse_from([
            "spt",
            "plugin",
            "restore",
            "dev.softpilot.fixture",
            "--version",
            "1.2.3",
        ])
        .expect("parse plugin restore");
        assert!(matches!(
            restore.command,
            Command::Plugin {
                command: PluginCommand::Restore {
                    plugin_id,
                    version,
                    json: false,
                }
            } if plugin_id == "dev.softpilot.fixture" && version == "1.2.3"
        ));

        let recover = Cli::try_parse_from(["spt", "plugin", "recover", "--json"])
            .expect("parse plugin recovery");
        assert!(matches!(
            recover.command,
            Command::Plugin {
                command: PluginCommand::Recover { json: true }
            }
        ));
    }

    #[test]
    fn json_envelopes_include_stable_success_and_error_fields() {
        let success: serde_json::Value = serde_json::from_str(
            &success_json(&serde_json::json!({ "value": 7 })).expect("serialize success"),
        )
        .expect("parse success envelope");
        assert_eq!(success["ok"], true);
        assert_eq!(success["data"]["value"], 7);

        let failure = CliFailure::message("test-code", "test.stage", 40, "test message");
        let serialized: serde_json::Value =
            serde_json::from_str(&failure_json(&failure).expect("serialize failure"))
                .expect("parse failure envelope");
        assert_eq!(serialized["ok"], false);
        assert_eq!(serialized["error"]["code"], "test-code");
        assert_eq!(serialized["error"]["stage"], "test.stage");
        assert_eq!(serialized["error"]["message"], "test message");
    }

    #[test]
    fn lock_timeout_has_a_distinct_stable_exit_classification() {
        let failure = workspace_failure(
            "plugin.install",
            WorkspaceError::LockTimeout {
                path: PathBuf::from("workspace.lock"),
                timeout_milliseconds: 75,
                holder: None,
            },
        );
        assert_eq!(failure.code, "workspace-locked");
        assert_eq!(failure.stage, "plugin.install");
        assert_eq!(failure.exit_code, 30);
    }
}
