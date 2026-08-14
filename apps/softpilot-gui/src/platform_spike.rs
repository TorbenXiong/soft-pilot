use std::{
    env, fs,
    fs::{File, OpenOptions, TryLockError},
    io,
    path::{Path, PathBuf},
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

use softpilot_plugin_api::inspect_component_descriptor;

pub(crate) fn try_run_probe(
    arguments: &[std::ffi::OsString],
) -> Result<bool, Box<dyn std::error::Error>> {
    let Some(command) = arguments.first().and_then(|value| value.to_str()) else {
        return Ok(false);
    };

    match command {
        "--child-probe" => Ok(true),
        "--lock-probe" => {
            let path = arguments
                .get(1)
                .ok_or("--lock-probe requires a lock file path")?;
            verify_lock_is_held(Path::new(path))?;
            Ok(true)
        }
        "--platform-spike" => {
            let component = arguments
                .get(1)
                .ok_or("--platform-spike requires a Component path")?;
            run_platform_spike(Path::new(component))?;
            Ok(true)
        }
        _ => Ok(false),
    }
}

pub(crate) fn filesystem_roots() -> Vec<PathBuf> {
    #[cfg(windows)]
    {
        (b'A'..=b'Z')
            .map(|letter| PathBuf::from(format!("{}:\\", char::from(letter))))
            .filter(|path| path.is_dir())
            .collect()
    }

    #[cfg(not(windows))]
    {
        vec![PathBuf::from("/")]
    }
}

fn run_platform_spike(component_path: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let executable = env::current_exe()?;
    verify_child_process(&executable)?;

    let scratch = ScratchDirectory::new()?;
    verify_cross_process_lock(&executable, scratch.path())?;
    verify_directory_link(scratch.path())?;

    let bytes = fs::read(component_path)?;
    let descriptor = inspect_component_descriptor(&bytes)?;

    println!("child-process: ok");
    println!("cross-process-lock: ok");
    println!("directory-link: ok");
    println!(
        "component: {} {} api {}",
        descriptor.id, descriptor.version, descriptor.plugin_api
    );
    Ok(())
}

fn verify_child_process(executable: &Path) -> io::Result<()> {
    let status = Command::new(executable).arg("--child-probe").status()?;
    if status.success() {
        Ok(())
    } else {
        Err(io::Error::other(format!(
            "child probe exited with {status}"
        )))
    }
}

fn verify_cross_process_lock(executable: &Path, scratch: &Path) -> io::Result<()> {
    let lock_path = scratch.join("workspace.lock");
    let lock = File::create(&lock_path)?;
    lock.lock()?;

    let status = Command::new(executable)
        .arg("--lock-probe")
        .arg(&lock_path)
        .status()?;
    lock.unlock()?;

    if status.success() {
        Ok(())
    } else {
        Err(io::Error::other(format!(
            "competing process did not observe the workspace lock: {status}"
        )))
    }
}

fn verify_lock_is_held(path: &Path) -> io::Result<()> {
    let competing = OpenOptions::new().read(true).write(true).open(path)?;
    match competing.try_lock() {
        Err(TryLockError::WouldBlock) => Ok(()),
        Err(TryLockError::Error(error)) => Err(error),
        Ok(()) => {
            competing.unlock()?;
            Err(io::Error::other(
                "competing process unexpectedly acquired the workspace lock",
            ))
        }
    }
}

#[cfg(windows)]
fn verify_directory_link(scratch: &Path) -> io::Result<()> {
    let target = scratch.join("target");
    let link = scratch.join("current");
    fs::create_dir(&target)?;

    let output = Command::new("cmd.exe")
        .args(["/d", "/c", "mklink", "/J"])
        .arg(&link)
        .arg(&target)
        .output()?;
    if !output.status.success() {
        return Err(io::Error::other(format!(
            "failed to create directory junction: {}",
            String::from_utf8_lossy(&output.stderr)
        )));
    }

    let resolved_link = fs::canonicalize(&link)?;
    let resolved_target = fs::canonicalize(&target)?;
    if resolved_link != resolved_target {
        return Err(io::Error::other("directory junction resolved incorrectly"));
    }
    fs::remove_dir(&link)?;
    Ok(())
}

#[cfg(unix)]
fn verify_directory_link(scratch: &Path) -> io::Result<()> {
    use std::os::unix::fs::symlink;

    let target = scratch.join("target");
    let link = scratch.join("current");
    fs::create_dir(&target)?;
    symlink(&target, &link)?;

    let resolved_link = fs::canonicalize(&link)?;
    let resolved_target = fs::canonicalize(&target)?;
    if resolved_link != resolved_target {
        return Err(io::Error::other("directory symlink resolved incorrectly"));
    }
    fs::remove_file(&link)?;
    Ok(())
}

struct ScratchDirectory(PathBuf);

impl ScratchDirectory {
    fn new() -> io::Result<Self> {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let path = env::temp_dir().join(format!("softpilot-spike-{}-{nonce}", std::process::id()));
        fs::create_dir(&path)?;
        Ok(Self(path))
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for ScratchDirectory {
    fn drop(&mut self) {
        let owned_name = self
            .0
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.starts_with("softpilot-spike-"));
        if owned_name {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
}
