use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

fn find_rc_exe() -> Option<PathBuf> {
    let kits_root = Path::new(r"C:\Program Files (x86)\Windows Kits\10\bin");
    if !kits_root.exists() {
        return None;
    }
    let mut versions: Vec<PathBuf> = fs::read_dir(kits_root)
        .ok()?
        .filter_map(|e| e.ok().map(|x| x.path()))
        .filter(|p| p.join("x64").join("rc.exe").exists())
        .collect();
    versions.sort();
    versions.pop().map(|p| p.join("x64").join("rc.exe"))
}

fn main() {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap());
    let repo_root = manifest_dir.parent().unwrap().parent().unwrap();
    let out_dir = PathBuf::from(env::var("OUT_DIR").unwrap());

    let payload_zip = env::var("IR_PAYLOAD_ZIP")
        .map(PathBuf::from)
        .unwrap_or_else(|_| out_dir.join("payload.zip"));
    println!("cargo:rerun-if-env-changed=IR_PAYLOAD_ZIP");
    if payload_zip.exists() {
        println!("cargo:rerun-if-changed={}", payload_zip.display());
    }
    println!("cargo:rustc-env=IR_PAYLOAD_ZIP={}", payload_zip.display());

    let icon_path = repo_root.join("assets").join("icons").join("icon.ico");
    println!("cargo:rerun-if-changed={}", icon_path.display());

    let app_manifest = out_dir.join("setup.manifest");
    fs::write(
        &app_manifest,
        r#"<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="InstantReplaySetup" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="urn:schemas-microsoft-com:SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
"#,
    )
    .unwrap();

    if let Some(rc_exe) = find_rc_exe() {
        let rc_file = out_dir.join("setup.rc");
        let res_file = out_dir.join("setup.res");
        let icon_escaped = icon_path.to_string_lossy().replace('\\', "\\\\");
        let man_escaped = app_manifest.to_string_lossy().replace('\\', "\\\\");
        fs::write(
            &rc_file,
            format!(
                "1 ICON \"{icon_escaped}\"\n1 24 \"{man_escaped}\"\n"
            ),
        )
        .unwrap();

        let status = Command::new(rc_exe)
            .args([
                "/nologo",
                "/fo",
                &res_file.to_string_lossy(),
                &rc_file.to_string_lossy(),
            ])
            .status();

        if matches!(status, Ok(s) if s.success()) && res_file.exists() {
            println!("cargo:rustc-link-arg-bins={}", res_file.display());
        }
    }
}
