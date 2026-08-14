use std::{
    env, fs,
    path::{Path, PathBuf},
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

use softpilot_engine::WorkspaceService;

struct TestDirectory(PathBuf);

impl TestDirectory {
    fn new() -> Self {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock")
            .as_nanos();
        let path = env::temp_dir().join(format!(
            "softpilot-spt-json-test-{}-{nonce}",
            std::process::id()
        ));
        fs::create_dir(&path).expect("create JSON CLI test directory");
        Self(path)
    }
}

impl Drop for TestDirectory {
    fn drop(&mut self) {
        if self.0.starts_with(env::temp_dir())
            && self.0.file_name().is_some_and(|name| {
                name.to_string_lossy()
                    .starts_with("softpilot-spt-json-test-")
            })
        {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
}

fn plugin_list(executable: &Path, workspace: &Path) -> Command {
    let mut command = Command::new(executable);
    command
        .arg("--workspace")
        .arg(workspace)
        .args(["plugin", "list", "--json"]);
    command
}

#[test]
fn json_output_uses_envelopes_and_stable_exit_codes() {
    let test = TestDirectory::new();
    let executable = PathBuf::from(env!("CARGO_BIN_EXE_spt"));
    let missing = plugin_list(&executable, &test.0.join("missing"))
        .output()
        .expect("run missing workspace command");
    assert_eq!(missing.status.code(), Some(10));
    let failure: serde_json::Value =
        serde_json::from_slice(&missing.stderr).expect("parse JSON error envelope");
    assert_eq!(failure["ok"], false);
    assert_eq!(failure["error"]["code"], "workspace-error");
    assert_eq!(failure["error"]["stage"], "workspace.resolve");

    let workspace = test.0.join("workspace");
    WorkspaceService::with_locations(
        test.0.join("portable.json"),
        Some(test.0.join("bootstrap.json")),
        None,
    )
    .initialize(&workspace)
    .expect("initialize JSON CLI workspace");
    let success = plugin_list(&executable, &workspace)
        .output()
        .expect("run plugin list command");
    assert!(
        success.status.success(),
        "{}",
        String::from_utf8_lossy(&success.stderr)
    );
    let envelope: serde_json::Value =
        serde_json::from_slice(&success.stdout).expect("parse JSON success envelope");
    assert_eq!(envelope["ok"], true);
    assert_eq!(envelope["data"], serde_json::json!([]));
}
