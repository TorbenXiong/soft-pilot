use std::{
    env, fs,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use softpilot_engine::WorkspaceService;

struct TestDirectory {
    path: PathBuf,
}

impl TestDirectory {
    fn new() -> Self {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock")
            .as_nanos();
        let path = env::temp_dir().join(format!(
            "softpilot-spt-lock-test-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&path).expect("create test directory");
        Self { path }
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        if self.path.starts_with(env::temp_dir())
            && self.path.file_name().is_some_and(|name| {
                name.to_string_lossy()
                    .starts_with("softpilot-spt-lock-test-")
            })
        {
            let _ = fs::remove_dir_all(&self.path);
        }
    }
}

fn lock_probe(executable: &Path, workspace: &Path, operation: &str, timeout_ms: u64) -> Command {
    let mut command = Command::new(executable);
    command
        .arg("--workspace")
        .arg(workspace)
        .args([
            "workspace",
            "lock-probe",
            "--operation",
            operation,
            "--timeout-ms",
        ])
        .arg(timeout_ms.to_string());
    command
}

#[test]
fn competing_process_reports_holder_then_acquires_after_release() {
    let test = TestDirectory::new();
    let workspace = test.path.join("workspace");
    let service = WorkspaceService::with_locations(
        test.path.join("portable.json"),
        Some(test.path.join("bootstrap.json")),
        None,
    );
    service
        .initialize(&workspace)
        .expect("initialize lock test workspace");

    let executable = PathBuf::from(env!("CARGO_BIN_EXE_spt"));
    let ready = test.path.join("holder-ready");
    let mut holder = lock_probe(&executable, &workspace, "integration.holder", 1_000);
    holder
        .args(["--hold-ms", "500"])
        .arg("--ready-file")
        .arg(&ready)
        .stdout(Stdio::null())
        .stderr(Stdio::piped());
    let mut holder = holder.spawn().expect("start holder process");

    let deadline = Instant::now() + Duration::from_secs(3);
    while !ready.exists() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(10));
    }
    assert!(ready.exists(), "holder process did not acquire the lock");

    let contender = lock_probe(&executable, &workspace, "integration.contender", 75)
        .output()
        .expect("run competing process");
    assert!(!contender.status.success());
    let stderr = String::from_utf8_lossy(&contender.stderr);
    assert!(stderr.contains("integration.holder"), "{stderr}");
    assert!(stderr.contains("timed out after 75 ms"), "{stderr}");

    assert!(holder.wait().expect("wait for holder process").success());
    let after_release = lock_probe(&executable, &workspace, "integration.after-release", 250)
        .output()
        .expect("run process after release");
    assert!(
        after_release.status.success(),
        "{}",
        String::from_utf8_lossy(&after_release.stderr)
    );
}
