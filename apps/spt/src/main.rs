use std::{path::PathBuf, process::ExitCode};

use clap::{Parser, Subcommand};
use softpilot_plugin_api::PluginPackageInspector;

#[derive(Debug, Parser)]
#[command(name = "spt", version, about = "SoftPilot cross-platform plugin host")]
struct Cli {
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
}

fn main() -> ExitCode {
    match run(Cli::parse()) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("error: {error}");
            ExitCode::FAILURE
        }
    }
}

fn run(cli: Cli) -> Result<(), Box<dyn std::error::Error>> {
    match cli.command {
        Command::Plugin {
            command: PluginCommand::Inspect { package, json },
        } => {
            let inspected = PluginPackageInspector::inspect(package)?;
            if json {
                println!("{}", serde_json::to_string_pretty(&inspected)?);
            } else {
                println!(
                    "Plugin: {} ({})",
                    inspected.manifest.name, inspected.manifest.id
                );
                println!("Version: {}", inspected.manifest.version);
                println!("Publisher: {}", inspected.manifest.publisher.name);
                println!("Package entries: {}", inspected.entries.len());
                println!(
                    "Wasm component validated: {}",
                    inspected.component_validated
                );
            }
        }
    }
    Ok(())
}
