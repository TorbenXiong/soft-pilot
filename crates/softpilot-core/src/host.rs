use std::{env, fmt, str::FromStr};

use serde::{Deserialize, Serialize};
use thiserror::Error;

use crate::{Architecture, Libc, OperatingSystem, PlatformTarget};

/// A supported native host target using the canonical Rust target-triple spelling.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
pub enum HostTriple {
    /// 64-bit x86 Windows using the MSVC ABI.
    #[serde(rename = "x86_64-pc-windows-msvc")]
    WindowsX86_64,
    /// 64-bit Arm Windows using the MSVC ABI.
    #[serde(rename = "aarch64-pc-windows-msvc")]
    WindowsArm64,
    /// 64-bit x86 macOS.
    #[serde(rename = "x86_64-apple-darwin")]
    MacOsX86_64,
    /// 64-bit Arm macOS.
    #[serde(rename = "aarch64-apple-darwin")]
    MacOsArm64,
    /// 64-bit x86 Linux using GNU libc.
    #[serde(rename = "x86_64-unknown-linux-gnu")]
    LinuxX86_64Glibc,
    /// 64-bit Arm Linux using GNU libc.
    #[serde(rename = "aarch64-unknown-linux-gnu")]
    LinuxArm64Glibc,
}

impl HostTriple {
    /// Detects the host target used to compile the running executable.
    ///
    /// # Errors
    ///
    /// Returns [`HostTripleError`] when the operating system, architecture, or Linux C runtime is
    /// outside the supported platform matrix.
    pub fn detect() -> Result<Self, HostTripleError> {
        Self::from_target_components(
            env::consts::OS,
            env::consts::ARCH,
            compiled_target_environment(),
        )
    }

    /// Returns the stable target-triple string used for host-specific workspace directories.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::WindowsX86_64 => "x86_64-pc-windows-msvc",
            Self::WindowsArm64 => "aarch64-pc-windows-msvc",
            Self::MacOsX86_64 => "x86_64-apple-darwin",
            Self::MacOsArm64 => "aarch64-apple-darwin",
            Self::LinuxX86_64Glibc => "x86_64-unknown-linux-gnu",
            Self::LinuxArm64Glibc => "aarch64-unknown-linux-gnu",
        }
    }

    /// Returns the plugin target corresponding to this host triple.
    #[must_use]
    pub const fn platform_target(self) -> PlatformTarget {
        match self {
            Self::WindowsX86_64 => PlatformTarget {
                os: OperatingSystem::Windows,
                architecture: Architecture::X86_64,
                libc: None,
            },
            Self::WindowsArm64 => PlatformTarget {
                os: OperatingSystem::Windows,
                architecture: Architecture::Arm64,
                libc: None,
            },
            Self::MacOsX86_64 => PlatformTarget {
                os: OperatingSystem::MacOs,
                architecture: Architecture::X86_64,
                libc: None,
            },
            Self::MacOsArm64 => PlatformTarget {
                os: OperatingSystem::MacOs,
                architecture: Architecture::Arm64,
                libc: None,
            },
            Self::LinuxX86_64Glibc => PlatformTarget {
                os: OperatingSystem::Linux,
                architecture: Architecture::X86_64,
                libc: Some(Libc::Glibc),
            },
            Self::LinuxArm64Glibc => PlatformTarget {
                os: OperatingSystem::Linux,
                architecture: Architecture::Arm64,
                libc: Some(Libc::Glibc),
            },
        }
    }

    fn from_target_components(
        operating_system: &str,
        architecture: &str,
        environment: &str,
    ) -> Result<Self, HostTripleError> {
        match (operating_system, architecture, environment) {
            ("windows", "x86_64", "msvc") => Ok(Self::WindowsX86_64),
            ("windows", "aarch64", "msvc") => Ok(Self::WindowsArm64),
            ("macos", "x86_64", _) => Ok(Self::MacOsX86_64),
            ("macos", "aarch64", _) => Ok(Self::MacOsArm64),
            ("linux", "x86_64", "gnu") => Ok(Self::LinuxX86_64Glibc),
            ("linux", "aarch64", "gnu") => Ok(Self::LinuxArm64Glibc),
            ("windows", "x86_64" | "aarch64", _) => Err(
                HostTripleError::UnsupportedWindowsEnvironment(environment.to_owned()),
            ),
            ("linux", "x86_64" | "aarch64", _) => Err(
                HostTripleError::UnsupportedLinuxEnvironment(environment.to_owned()),
            ),
            ("windows" | "macos" | "linux", _, _) => Err(HostTripleError::UnsupportedArchitecture(
                architecture.to_owned(),
            )),
            _ => Err(HostTripleError::UnsupportedOperatingSystem(
                operating_system.to_owned(),
            )),
        }
    }
}

impl fmt::Display for HostTriple {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.as_str())
    }
}

impl FromStr for HostTriple {
    type Err = HostTripleError;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value {
            "x86_64-pc-windows-msvc" => Ok(Self::WindowsX86_64),
            "aarch64-pc-windows-msvc" => Ok(Self::WindowsArm64),
            "x86_64-apple-darwin" => Ok(Self::MacOsX86_64),
            "aarch64-apple-darwin" => Ok(Self::MacOsArm64),
            "x86_64-unknown-linux-gnu" => Ok(Self::LinuxX86_64Glibc),
            "aarch64-unknown-linux-gnu" => Ok(Self::LinuxArm64Glibc),
            _ => Err(HostTripleError::InvalidTriple(value.to_owned())),
        }
    }
}

/// Detection or parsing error for a host triple.
#[derive(Debug, Error, PartialEq, Eq)]
pub enum HostTripleError {
    /// The operating system is outside the supported host matrix.
    #[error("unsupported host operating system '{0}'")]
    UnsupportedOperatingSystem(String),
    /// The CPU architecture is outside the supported host matrix.
    #[error("unsupported host architecture '{0}'")]
    UnsupportedArchitecture(String),
    /// Windows does not use the supported MSVC ABI.
    #[error("unsupported Windows target environment '{0}'; MSVC is required")]
    UnsupportedWindowsEnvironment(String),
    /// Linux does not use the supported GNU libc environment.
    #[error("unsupported Linux target environment '{0}'; GNU libc is required")]
    UnsupportedLinuxEnvironment(String),
    /// A persisted target-triple string is not recognized.
    #[error("unsupported host triple '{0}'")]
    InvalidTriple(String),
}

const fn compiled_target_environment() -> &'static str {
    #[cfg(target_env = "gnu")]
    {
        "gnu"
    }
    #[cfg(target_env = "msvc")]
    {
        "msvc"
    }
    #[cfg(target_env = "musl")]
    {
        "musl"
    }
    #[cfg(not(any(target_env = "gnu", target_env = "msvc", target_env = "musl")))]
    {
        ""
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn supported_triples_round_trip_and_map_to_platform_targets() {
        let cases = [
            (
                "x86_64-pc-windows-msvc",
                HostTriple::WindowsX86_64,
                OperatingSystem::Windows,
                Architecture::X86_64,
                None,
            ),
            (
                "aarch64-pc-windows-msvc",
                HostTriple::WindowsArm64,
                OperatingSystem::Windows,
                Architecture::Arm64,
                None,
            ),
            (
                "x86_64-apple-darwin",
                HostTriple::MacOsX86_64,
                OperatingSystem::MacOs,
                Architecture::X86_64,
                None,
            ),
            (
                "aarch64-apple-darwin",
                HostTriple::MacOsArm64,
                OperatingSystem::MacOs,
                Architecture::Arm64,
                None,
            ),
            (
                "x86_64-unknown-linux-gnu",
                HostTriple::LinuxX86_64Glibc,
                OperatingSystem::Linux,
                Architecture::X86_64,
                Some(Libc::Glibc),
            ),
            (
                "aarch64-unknown-linux-gnu",
                HostTriple::LinuxArm64Glibc,
                OperatingSystem::Linux,
                Architecture::Arm64,
                Some(Libc::Glibc),
            ),
        ];

        for (value, triple, os, architecture, libc) in cases {
            assert_eq!(value.parse::<HostTriple>(), Ok(triple));
            assert_eq!(triple.to_string(), value);
            assert_eq!(
                triple.platform_target(),
                PlatformTarget {
                    os,
                    architecture,
                    libc,
                }
            );
        }
    }

    #[test]
    fn detection_rejects_unknown_platform_components() {
        assert!(matches!(
            HostTriple::from_target_components("freebsd", "x86_64", ""),
            Err(HostTripleError::UnsupportedOperatingSystem(_))
        ));
        assert!(matches!(
            HostTriple::from_target_components("linux", "riscv64", "gnu"),
            Err(HostTripleError::UnsupportedArchitecture(_))
        ));
        assert_eq!(
            HostTriple::from_target_components("linux", "x86_64", "musl"),
            Err(HostTripleError::UnsupportedLinuxEnvironment(
                "musl".to_owned()
            ))
        );
        assert_eq!(
            HostTriple::from_target_components("windows", "x86_64", "gnu"),
            Err(HostTripleError::UnsupportedWindowsEnvironment(
                "gnu".to_owned()
            ))
        );
    }

    #[test]
    fn detects_the_compiled_host() {
        let detected = HostTriple::detect().expect("test runner must be a supported host");
        let expected = if cfg!(all(target_os = "windows", target_arch = "x86_64")) {
            HostTriple::WindowsX86_64
        } else if cfg!(all(target_os = "windows", target_arch = "aarch64")) {
            HostTriple::WindowsArm64
        } else if cfg!(all(target_os = "macos", target_arch = "x86_64")) {
            HostTriple::MacOsX86_64
        } else if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
            HostTriple::MacOsArm64
        } else if cfg!(all(
            target_os = "linux",
            target_arch = "x86_64",
            target_env = "gnu"
        )) {
            HostTriple::LinuxX86_64Glibc
        } else if cfg!(all(
            target_os = "linux",
            target_arch = "aarch64",
            target_env = "gnu"
        )) {
            HostTriple::LinuxArm64Glibc
        } else {
            panic!("unsupported test host")
        };
        assert_eq!(detected, expected);
    }
}
