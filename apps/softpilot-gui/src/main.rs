#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod platform_spike;

use std::{
    env, fs,
    path::{Path, PathBuf},
    process::ExitCode,
    rc::Rc,
    time::Duration,
};

use slint::{ModelRc, SharedString, VecModel};

slint::include_modules!();

fn main() -> ExitCode {
    let arguments = env::args_os().skip(1).collect::<Vec<_>>();
    match run(&arguments) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("error: {error}");
            ExitCode::FAILURE
        }
    }
}

fn run(arguments: &[std::ffi::OsString]) -> Result<(), Box<dyn std::error::Error>> {
    if platform_spike::try_run_probe(arguments)? {
        return Ok(());
    }

    let window_smoke = arguments
        .first()
        .is_some_and(|argument| argument == "--window-smoke");
    let workspace_smoke = arguments
        .first()
        .is_some_and(|argument| argument == "--workspace-smoke");

    let window = MainWindow::new()?;
    let initial_directory = default_directory()?;
    show_directory(&window, &initial_directory);

    let weak = window.as_weak();
    window.on_navigate(move |path| {
        if let Some(window) = weak.upgrade() {
            show_directory(&window, Path::new(path.as_str()));
        }
    });

    let weak = window.as_weak();
    window.on_navigate_parent(move || {
        if let Some(window) = weak.upgrade() {
            let current = PathBuf::from(window.get_current_directory().as_str());
            if let Some(parent) = current.parent() {
                show_directory(&window, parent);
            }
        }
    });

    let weak = window.as_weak();
    window.on_show_roots(move || {
        if let Some(window) = weak.upgrade() {
            show_roots(&window);
        }
    });

    let weak = window.as_weak();
    window.on_select_workspace(move |path| {
        if let Some(window) = weak.upgrade() {
            let selected = PathBuf::from(path.as_str());
            if selected.is_dir() {
                window.set_status_text(format!("已选择工作区：{}", selected.display()).into());
            } else {
                window.set_status_text("所选路径不是可访问目录".into());
            }
        }
    });

    if workspace_smoke {
        let selected = arguments
            .get(1)
            .ok_or("--workspace-smoke requires a workspace directory")?;
        let selected = PathBuf::from(selected);
        show_directory(&window, &selected);
        window.invoke_select_workspace(selected.to_string_lossy().into_owned().into());

        let expected = format!("已选择工作区：{}", selected.display());
        let actual = window.get_status_text();
        if actual.as_str() != expected.as_str() {
            return Err(format!("workspace selection returned unexpected status: {actual}").into());
        }

        println!("workspace-selection: ok");
        return Ok(());
    }

    if window_smoke {
        slint::Timer::single_shot(Duration::from_millis(500), || {
            let _ = slint::quit_event_loop();
        });
    }
    window.run()?;
    Ok(())
}

fn default_directory() -> Result<PathBuf, Box<dyn std::error::Error>> {
    #[cfg(windows)]
    let profile = env::var_os("USERPROFILE");
    #[cfg(not(windows))]
    let profile = env::var_os("HOME");

    match profile {
        Some(path) => Ok(PathBuf::from(path)),
        None => Ok(env::current_dir()?),
    }
}

fn show_directory(window: &MainWindow, requested: &Path) {
    let canonical = match fs::canonicalize(requested) {
        Ok(path) if path.is_dir() => path,
        Ok(_) => {
            window.set_status_text("路径不是目录".into());
            return;
        }
        Err(error) => {
            window.set_status_text(format!("无法打开目录：{error}").into());
            return;
        }
    };

    let mut directories = match fs::read_dir(&canonical) {
        Ok(entries) => entries
            .filter_map(Result::ok)
            .filter_map(|entry| {
                entry
                    .file_type()
                    .ok()
                    .filter(std::fs::FileType::is_dir)
                    .map(|_| {
                        let path = entry.path();
                        DirectoryItem {
                            name: entry.file_name().to_string_lossy().into_owned().into(),
                            path: path.to_string_lossy().into_owned().into(),
                        }
                    })
            })
            .collect::<Vec<_>>(),
        Err(error) => {
            window.set_status_text(format!("无法列出目录：{error}").into());
            return;
        }
    };
    directories.sort_by(|left, right| {
        left.name
            .as_str()
            .to_lowercase()
            .cmp(&right.name.as_str().to_lowercase())
    });

    window.set_current_directory(canonical.to_string_lossy().into_owned().into());
    set_directories(window, directories);
    window.set_status_text("请选择当前目录或进入子目录".into());
}

fn show_roots(window: &MainWindow) {
    let roots = platform_spike::filesystem_roots()
        .into_iter()
        .map(|path| DirectoryItem {
            name: path.to_string_lossy().into_owned().into(),
            path: path.to_string_lossy().into_owned().into(),
        })
        .collect();
    window.set_current_directory(SharedString::default());
    set_directories(window, roots);
    window.set_status_text("请选择文件系统根目录".into());
}

fn set_directories(window: &MainWindow, directories: Vec<DirectoryItem>) {
    let model = Rc::new(VecModel::from(directories));
    window.set_directories(ModelRc::from(model));
}
