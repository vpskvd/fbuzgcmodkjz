#![windows_subsystem = "windows"]

use std::env;
use std::fs::{self, File};
use std::io::{Cursor, Read, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;

use windows::core::{Interface, PCWSTR};
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, CreateFontW, CreateSolidBrush, DeleteObject, EndPaint, FillRect, GetStockObject,
    InvalidateRect, SetBkMode, SetTextColor, DEFAULT_GUI_FONT, HBRUSH, HDC, HFONT, PAINTSTRUCT,
    TRANSPARENT,
};
use windows::Win32::Networking::WinHttp::{
    WinHttpCloseHandle, WinHttpConnect, WinHttpOpen, WinHttpOpenRequest, WinHttpQueryDataAvailable,
    WinHttpReadData, WinHttpReceiveResponse, WinHttpSendRequest, WinHttpSetTimeouts,
    WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_FLAG_SECURE,
};
use windows::Win32::System::Com::{
    CoCreateInstance, CoInitializeEx, CoUninitialize, IPersistFile, CLSCTX_INPROC_SERVER,
    COINIT_APARTMENTTHREADED,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Registry::{
    RegCloseKey, RegCreateKeyExW, RegDeleteTreeW, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW,
    RegSetValueExW, HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WRITE,
    REG_OPTION_NON_VOLATILE, REG_DWORD, REG_SZ,
};
use windows::Win32::System::Com::Urlmon::URLDownloadToFileW;
use windows::Win32::UI::Controls::BST_CHECKED;
use windows::Win32::UI::Input::KeyboardAndMouse::EnableWindow;
use windows::Win32::UI::Shell::{IShellLinkW, ShellLink};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect, GetMessageW,
    GetSystemMetrics, LoadCursorW, LoadIconW, MessageBoxW, PostMessageW, PostQuitMessage,
    RegisterClassW, SendMessageW, SetWindowTextW, ShowWindow, TranslateMessage, BM_GETCHECK,
    BM_SETCHECK, BS_AUTOCHECKBOX, BS_DEFPUSHBUTTON, BS_PUSHBUTTON, CS_HREDRAW, CS_VREDRAW,
    HICON, IDC_ARROW, MB_ICONERROR, MB_ICONINFORMATION, MB_OK, MSG, SM_CXSCREEN,
    SM_CYSCREEN, SW_SHOW, WM_APP, WM_CLOSE, WM_COMMAND, WM_CREATE, WM_CTLCOLORBTN,
    WM_CTLCOLORSTATIC, WM_DESTROY, WM_PAINT, WM_SETFONT, WNDCLASSW, WS_CAPTION, WS_CHILD,
    WS_CLIPCHILDREN, WS_MINIMIZEBOX, WS_SYSMENU, WS_TABSTOP, WS_VISIBLE,
};

static PAYLOAD_BYTES: &[u8] = include_bytes!(env!("IR_PAYLOAD_ZIP"));

const APP_VERSION: &str = "1.0.0";
const CREATE_NO_WINDOW: u32 = 0x08000000;

const ID_BTN_PRIMARY: usize = 101;
const ID_BTN_CANCEL: usize = 102;
const ID_CHK_DESKTOP: usize = 103;
const ID_CHK_LAUNCH: usize = 104;

const WM_SETUP_PROGRESS: u32 = WM_APP + 1;
const WM_SETUP_DONE: u32 = WM_APP + 2;
const WM_SETUP_ERROR: u32 = WM_APP + 3;

const BG_COLOR: u32 = 0x001D1816; // BGR for #16181D
const CARD_COLOR: u32 = 0x0028221F; // BGR for #1F2228
const BORDER_COLOR: u32 = 0x003D3531;
const TRACK_COLOR: u32 = 0x00362F2B;
const FILL_COLOR: u32 = 0x00EEF2F6; // light neutral accent
const TEXT_PRIMARY: u32 = 0x00F6F2F0;
const TEXT_SECONDARY: u32 = 0x00ACA39C;

struct UiState {
    hwnd: HWND,
    btn_primary: HWND,
    btn_cancel: HWND,
    chk_desktop: HWND,
    chk_launch: HWND,
    font_title: HFONT,
    font_body: HFONT,
    font_small: HFONT,
    bg_brush: HBRUSH,
    card_brush: HBRUSH,
    status_text: String,
    detail_text: String,
    progress_pct: u32,
    installing: bool,
    finished: bool,
    update_mode: bool,
    create_desktop_shortcut: bool,
    launch_after: bool,
    is_upgrade: bool,
}

static mut UI_STATE: Option<UiState> = None;
static INSTALL_ABORTED: AtomicBool = AtomicBool::new(false);

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn install_dir() -> PathBuf {
    let local = env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
    local.join("Programs").join("InstantReplay")
}

fn start_menu_shortcut() -> Option<PathBuf> {
    env::var_os("APPDATA").map(|p| {
        PathBuf::from(p)
            .join("Microsoft")
            .join("Windows")
            .join("Start Menu")
            .join("Programs")
            .join("Instant Replay.lnk")
    })
}

fn desktop_shortcut() -> Option<PathBuf> {
    env::var_os("USERPROFILE").map(|p| PathBuf::from(p).join("Desktop").join("Instant Replay.lnk"))
}

fn parse_ver(s: &str) -> Option<(u32, u32, u32)> {
    let clean = s.trim().trim_start_matches('v').split('-').next()?;
    let mut parts = clean.split('.');
    let a = parts.next()?.parse().ok()?;
    let b = parts.next()?.parse().ok()?;
    let c = parts.next().and_then(|x| x.parse().ok()).unwrap_or(0);
    Some((a, b, c))
}

fn find_installed_dotnet8() -> Option<(u32, u32, u32)> {
    let mut best: Option<(u32, u32, u32)> = None;
    let mut roots: Vec<PathBuf> = Vec::new();
    if let Some(pf) = env::var_os("ProgramFiles") {
        roots.push(PathBuf::from(pf).join("dotnet"));
    }
    if let Some(dr) = env::var_os("DOTNET_ROOT") {
        roots.push(PathBuf::from(dr));
    }
    if let Some(la) = env::var_os("LOCALAPPDATA") {
        roots.push(PathBuf::from(la).join("Microsoft").join("dotnet"));
    }

    for root in roots {
        let shared = root.join("shared").join("Microsoft.WindowsDesktop.App");
        if let Ok(entries) = fs::read_dir(&shared) {
            for entry in entries.flatten() {
                let name = entry.file_name();
                if let Some(v) = parse_ver(&name.to_string_lossy()) {
                    if v.0 == 8 && entry.path().join("System.Windows.Forms.dll").exists() {
                        if best.map_or(true, |b| v > b) {
                            best = Some(v);
                        }
                    }
                }
            }
        }
    }

    if let Ok(out) = Command::new("dotnet")
        .arg("--list-runtimes")
        .creation_flags(CREATE_NO_WINDOW)
        .output()
    {
        let stdout = String::from_utf8_lossy(&out.stdout);
        for line in stdout.lines() {
            if let Some(rest) = line.strip_prefix("Microsoft.WindowsDesktop.App ") {
                if let Some(ver_str) = rest.split_whitespace().next() {
                    if let Some(v) = parse_ver(ver_str) {
                        if v.0 == 8 && best.map_or(true, |b| v > b) {
                            best = Some(v);
                        }
                    }
                }
            }
        }
    }
    best
}

fn fetch_latest_dotnet8_version() -> Option<(u32, u32, u32)> {
    unsafe {
        let agent = to_wide("InstantReplaySetup/1.0");
        let h_session = WinHttpOpen(
            PCWSTR(agent.as_ptr()),
            WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
            PCWSTR::null(),
            PCWSTR::null(),
            0,
        );
        if h_session.is_null() {
            return None;
        }
        let _ = WinHttpSetTimeouts(h_session, 4000, 4000, 4000, 5000);
        let host = to_wide("dotnetcli.blob.core.windows.net");
        let h_connect = WinHttpConnect(h_session, PCWSTR(host.as_ptr()), 443, 0);
        if h_connect.is_null() {
            let _ = WinHttpCloseHandle(h_session);
            return None;
        }
        let path = to_wide("/dotnet/release-metadata/8.0/releases.json");
        let verb = to_wide("GET");
        let h_req = WinHttpOpenRequest(
            h_connect,
            PCWSTR(verb.as_ptr()),
            PCWSTR(path.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            std::ptr::null(),
            WINHTTP_FLAG_SECURE,
        );
        if h_req.is_null() {
            let _ = WinHttpCloseHandle(h_connect);
            let _ = WinHttpCloseHandle(h_session);
            return None;
        }

        let mut body = Vec::new();
        if WinHttpSendRequest(h_req, None, None, 0, 0, 0).is_ok()
            && WinHttpReceiveResponse(h_req, std::ptr::null_mut()).is_ok()
        {
            while body.len() < 4096 {
                let mut avail = 0u32;
                if WinHttpQueryDataAvailable(h_req, &mut avail).is_err() || avail == 0 {
                    break;
                }
                let to_read = avail.min(2048) as usize;
                let mut chunk = vec![0u8; to_read];
                let mut read = 0u32;
                if WinHttpReadData(h_req, chunk.as_mut_ptr() as *mut _, to_read as u32, &mut read)
                    .is_err()
                    || read == 0
                {
                    break;
                }
                body.extend_from_slice(&chunk[..read as usize]);
            }
        }

        let _ = WinHttpCloseHandle(h_req);
        let _ = WinHttpCloseHandle(h_connect);
        let _ = WinHttpCloseHandle(h_session);

        let text = String::from_utf8_lossy(&body);
        if let Some(pos) = text.find("\"latest-runtime\"") {
            let after = &text[pos + "\"latest-runtime\"".len()..];
            if let Some(q1) = after.find('"') {
                let rest = &after[q1 + 1..];
                if let Some(q2) = rest.find('"') {
                    return parse_ver(&rest[..q2]);
                }
            }
        }
        None
    }
}

fn is_webview2_installed() -> bool {
    let subkey = to_wide(r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
    let subkey64 = to_wide(r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
    let val_name = to_wide("pv");

    for (hive, key_path) in [
        (HKEY_LOCAL_MACHINE, &subkey),
        (HKEY_LOCAL_MACHINE, &subkey64),
        (HKEY_CURRENT_USER, &subkey64),
    ] {
        unsafe {
            let mut hkey = HKEY::default();
            if RegOpenKeyExW(hive, PCWSTR(key_path.as_ptr()), Some(0), KEY_READ, &mut hkey).is_ok() {
                let mut buf = [0u16; 64];
                let mut size = (buf.len() * 2) as u32;
                let ok = RegQueryValueExW(
                    hkey,
                    PCWSTR(val_name.as_ptr()),
                    None,
                    None,
                    Some(buf.as_mut_ptr() as *mut u8),
                    Some(&mut size),
                )
                .is_ok();
                let _ = RegCloseKey(hkey);
                if ok && size > 4 {
                    let s = String::from_utf16_lossy(&buf[..(size as usize / 2).saturating_sub(1)]);
                    if !s.is_empty() && s != "0.0.0.0" {
                        return true;
                    }
                }
            }
        }
    }
    false
}

fn download_file(url: &str, dest: &Path) -> Result<(), String> {
    let url_w = to_wide(url);
    let dest_w = to_wide(&dest.to_string_lossy());
    unsafe {
        URLDownloadToFileW(None, PCWSTR(url_w.as_ptr()), PCWSTR(dest_w.as_ptr()), 0, None)
            .map_err(|e| format!("download failed: {e}"))?;
    }
    if !dest.exists() || fs::metadata(dest).map(|m| m.len()).unwrap_or(0) < 32768 {
        return Err("downloaded installer is incomplete".to_string());
    }
    Ok(())
}

fn ensure_dotnet8(hwnd: HWND, silent: bool) -> Result<(), String> {
    let installed = find_installed_dotnet8();
    let latest = fetch_latest_dotnet8_version();

    let needs_install = match (installed, latest) {
        (None, _) => true,
        (Some(cur), Some(lat)) => cur < lat,
        (Some(_), None) => false,
    };

    if !needs_install {
        return Ok(());
    }

    let msg = match installed {
        None => "installing .net 8 desktop runtime...",
        Some(_) => "updating .net 8 desktop runtime...",
    };
    post_progress(hwnd, 10, msg);

    let tmp = env::temp_dir().join("ir_windowsdesktop_runtime_x64.exe");
    let _ = fs::remove_file(&tmp);
    download_file("https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe", &tmp)?;
    post_progress(hwnd, 22, msg);

    let mode_arg = if silent { "/quiet" } else { "/passive" };
    let status = Command::new(&tmp)
        .args(["/install", mode_arg, "/norestart"])
        .status()
        .map_err(|e| format!("failed launching .net installer: {e}"))?;
    let _ = fs::remove_file(&tmp);

    let code = status.code().unwrap_or(-1);
    if code != 0 && code != 3010 && code != 1638 {
        if find_installed_dotnet8().is_none() {
            return Err(format!(".net 8 desktop runtime install exited with code {code}"));
        }
    }
    Ok(())
}

fn ensure_webview2(hwnd: HWND) {
    if is_webview2_installed() {
        return;
    }
    post_progress(hwnd, 30, "installing webview2 runtime...");
    let tmp = env::temp_dir().join("ir_webview2_bootstrapper.exe");
    let _ = fs::remove_file(&tmp);
    if download_file("https://go.microsoft.com/fwlink/p/?LinkId=2124703", &tmp).is_ok() {
        let _ = Command::new(&tmp).args(["/silent", "/install"]).status();
        let _ = fs::remove_file(&tmp);
    }
}

fn kill_running_replay() {
    let _ = Command::new("taskkill")
        .args(["/IM", "replay.exe", "/F", "/T"])
        .creation_flags(CREATE_NO_WINDOW)
        .status();
    for _ in 0..15 {
        if let Ok(out) = Command::new("tasklist")
            .args(["/FI", "IMAGENAME eq replay.exe", "/NH"])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        {
            if !String::from_utf8_lossy(&out.stdout)
                .to_ascii_lowercase()
                .contains("replay.exe")
            {
                break;
            }
        }
        thread::sleep(std::time::Duration::from_millis(200));
    }
}

fn create_shortcut(lnk_path: &Path, target_exe: &Path, work_dir: &Path) {
    if let Some(parent) = lnk_path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        if let Ok(link) = CoCreateInstance::<_, IShellLinkW>(&ShellLink, None, CLSCTX_INPROC_SERVER) {
            let tgt = to_wide(&target_exe.to_string_lossy());
            let dir = to_wide(&work_dir.to_string_lossy());
            let desc = to_wide("Instant Replay");
            let _ = link.SetPath(PCWSTR(tgt.as_ptr()));
            let _ = link.SetWorkingDirectory(PCWSTR(dir.as_ptr()));
            let _ = link.SetDescription(PCWSTR(desc.as_ptr()));
            let _ = link.SetIconLocation(PCWSTR(tgt.as_ptr()), 0);
            if let Ok(pf) = link.cast::<IPersistFile>() {
                let lnk_w = to_wide(&lnk_path.to_string_lossy());
                let _ = pf.Save(PCWSTR(lnk_w.as_ptr()), true);
            }
        }
        CoUninitialize();
    }
}

fn set_reg_sz(hkey: HKEY, name: &str, val: &str) {
    let n = to_wide(name);
    let v = to_wide(val);
    let bytes = unsafe { std::slice::from_raw_parts(v.as_ptr() as *const u8, v.len() * 2) };
    unsafe {
        let _ = RegSetValueExW(hkey, PCWSTR(n.as_ptr()), Some(0), REG_SZ, Some(bytes));
    }
}

fn set_reg_dword(hkey: HKEY, name: &str, val: u32) {
    let n = to_wide(name);
    let bytes = val.to_le_bytes();
    unsafe {
        let _ = RegSetValueExW(hkey, PCWSTR(n.as_ptr()), Some(0), REG_DWORD, Some(&bytes));
    }
}

fn register_uninstall(dir: &Path, uninstaller_exe: &Path) {
    let sub = to_wide(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\InstantReplay");
    unsafe {
        let mut hkey = HKEY::default();
        if RegCreateKeyExW(
            HKEY_CURRENT_USER,
            PCWSTR(sub.as_ptr()),
            Some(0),
            PCWSTR::null(),
            REG_OPTION_NON_VOLATILE,
            KEY_WRITE,
            None,
            &mut hkey,
            None,
        )
        .is_ok()
        {
            let exe = dir.join("replay.exe");
            set_reg_sz(hkey, "DisplayName", "instant replay");
            set_reg_sz(hkey, "DisplayVersion", APP_VERSION);
            set_reg_sz(hkey, "Publisher", "instant replay");
            set_reg_sz(hkey, "InstallLocation", &dir.to_string_lossy());
            set_reg_sz(hkey, "DisplayIcon", &exe.to_string_lossy());
            set_reg_sz(
                hkey,
                "UninstallString",
                &format!("\"{}\" --uninstall", uninstaller_exe.to_string_lossy()),
            );
            set_reg_dword(hkey, "NoModify", 1);
            set_reg_dword(hkey, "NoRepair", 1);
            let _ = RegCloseKey(hkey);
        }
    }
}

fn run_install(hwnd: HWND, create_desktop: bool, silent_runtime: bool) -> Result<(), String> {
    post_progress(hwnd, 4, "checking .net 8 desktop runtime...");
    ensure_dotnet8(hwnd, silent_runtime)?;
    if INSTALL_ABORTED.load(Ordering::Relaxed) {
        return Err("cancelled".to_string());
    }

    ensure_webview2(hwnd);
    if INSTALL_ABORTED.load(Ordering::Relaxed) {
        return Err("cancelled".to_string());
    }

    post_progress(hwnd, 34, "closing running instance...");
    kill_running_replay();

    let target_dir = install_dir();
    fs::create_dir_all(&target_dir).map_err(|e| format!("cannot create install folder: {e}"))?;

    let cursor = Cursor::new(PAYLOAD_BYTES);
    let mut archive =
        zip::ZipArchive::new(cursor).map_err(|e| format!("corrupt installer payload: {e}"))?;
    let total = archive.len().max(1);

    for i in 0..archive.len() {
        if INSTALL_ABORTED.load(Ordering::Relaxed) {
            return Err("cancelled".to_string());
        }
        let mut entry = archive
            .by_index(i)
            .map_err(|e| format!("archive error: {e}"))?;
        let Some(rel) = entry.enclosed_name().map(|p| p.to_path_buf()) else {
            continue;
        };
        let out_path = target_dir.join(&rel);
        if entry.is_dir() {
            let _ = fs::create_dir_all(&out_path);
        } else {
            if let Some(parent) = out_path.parent() {
                fs::create_dir_all(parent).map_err(|e| format!("mkdir failed: {e}"))?;
            }
            let mut buf = Vec::with_capacity(entry.size() as usize);
            entry
                .read_to_end(&mut buf)
                .map_err(|e| format!("read failed: {e}"))?;

            let mut last_err = None;
            for _ in 0..5 {
                match File::create(&out_path).and_then(|mut f| f.write_all(&buf)) {
                    Ok(()) => {
                        last_err = None;
                        break;
                    }
                    Err(e) => {
                        last_err = Some(e);
                        thread::sleep(std::time::Duration::from_millis(200));
                    }
                }
            }
            if let Some(e) = last_err {
                return Err(format!("could not write {}: {e}", rel.display()));
            }
        }

        if i % 4 == 0 || i + 1 == total {
            let pct = 35 + (((i + 1) * 58) / total) as u32;
            post_progress(hwnd, pct.min(93), "installing instant replay...");
        }
    }

    post_progress(hwnd, 95, "creating shortcuts...");
    let app_exe = target_dir.join("replay.exe");
    if let Some(sm) = start_menu_shortcut() {
        create_shortcut(&sm, &app_exe, &target_dir);
    }
    if create_desktop {
        if let Some(dk) = desktop_shortcut() {
            create_shortcut(&dk, &app_exe, &target_dir);
        }
    }

    let uninstaller_exe = target_dir.join("uninstall.exe");
    if let Ok(self_exe) = env::current_exe() {
        if !self_exe
            .to_string_lossy()
            .eq_ignore_ascii_case(&uninstaller_exe.to_string_lossy())
        {
            let _ = fs::copy(&self_exe, &uninstaller_exe);
        }
    }
    register_uninstall(&target_dir, &uninstaller_exe);

    post_progress(hwnd, 100, "installed");
    Ok(())
}

fn run_uninstall(silent: bool) {
    kill_running_replay();

    if let Some(sm) = start_menu_shortcut() {
        let _ = fs::remove_file(sm);
    }
    if let Some(dk) = desktop_shortcut() {
        let _ = fs::remove_file(dk);
    }

    unsafe {
        let uninst = to_wide(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\InstantReplay");
        let _ = RegDeleteTreeW(HKEY_CURRENT_USER, PCWSTR(uninst.as_ptr()));

        let run_key = to_wide(r"Software\Microsoft\Windows\CurrentVersion\Run");
        let mut hkey = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(run_key.as_ptr()), Some(0), KEY_WRITE, &mut hkey).is_ok() {
            let val = to_wide("ReplayBuffer");
            let _ = RegDeleteValueW(hkey, PCWSTR(val.as_ptr()));
            let _ = RegCloseKey(hkey);
        }
    }

    let dir = install_dir();
    if dir.exists() {
        let dir_str = dir.to_string_lossy();
        let raw = format!(
            "/C \"ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir_str}\" & if exist \"{dir_str}\" (ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dir_str}\")\""
        );
        let _ = Command::new("cmd.exe")
            .raw_arg(raw)
            .current_dir(env::temp_dir())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn();
    }

    if !silent {
        unsafe {
            let title = to_wide("instant replay setup");
            let msg = to_wide("instant replay has been uninstalled.\nyour saved replay videos and settings were kept.");
            MessageBoxW(None, PCWSTR(msg.as_ptr()), PCWSTR(title.as_ptr()), MB_OK | MB_ICONINFORMATION);
        }
    }
}

fn launch_installed_app() {
    let dir = install_dir();
    let exe = dir.join("replay.exe");
    if exe.exists() {
        let _ = Command::new(&exe).current_dir(&dir).spawn();
    }
}

static mut NEXT_MSG_STATUS: Option<String> = None;

fn post_progress(hwnd: HWND, pct: u32, text: &str) {
    unsafe {
        NEXT_MSG_STATUS = Some(text.to_string());
        let _ = PostMessageW(Some(hwnd), WM_SETUP_PROGRESS, WPARAM(pct as usize), LPARAM(0));
    }
}

fn scale_px(base: i32, dpi: i32) -> i32 {
    (base * dpi + 48) / 96
}

unsafe fn set_ctrl_font(ctrl: HWND, font: HFONT) {
    let _ = SendMessageW(ctrl, WM_SETFONT, Some(WPARAM(font.0 as usize)), Some(LPARAM(1)));
}

unsafe fn start_install_worker(hwnd: HWND) {
    let st = UI_STATE.as_mut().unwrap();
    if st.installing || st.finished {
        return;
    }
    st.installing = true;
    st.create_desktop_shortcut = SendMessageW(st.chk_desktop, BM_GETCHECK, None, None).0 == BST_CHECKED.0 as isize;
    st.launch_after = SendMessageW(st.chk_launch, BM_GETCHECK, None, None).0 == BST_CHECKED.0 as isize;
    let create_desk = st.create_desktop_shortcut;
    let silent_rt = st.update_mode;

    let _ = EnableWindow(st.btn_primary, false);
    let _ = EnableWindow(st.chk_desktop, false);
    let _ = EnableWindow(st.chk_launch, false);
    let hwnd_raw = hwnd.0 as usize;

    thread::spawn(move || {
        let hwnd_send = HWND(hwnd_raw as *mut _);
        match run_install(hwnd_send, create_desk, silent_rt) {
            Ok(()) => {
                let _ = PostMessageW(Some(hwnd_send), WM_SETUP_DONE, WPARAM(0), LPARAM(0));
            }
            Err(e) => {
                NEXT_MSG_STATUS = Some(e);
                let _ = PostMessageW(Some(hwnd_send), WM_SETUP_ERROR, WPARAM(0), LPARAM(0));
            }
        }
    });
}

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_CREATE => {
            let dark: i32 = 1;
            let _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark as *const _ as *const _, 4);
            LRESULT(0)
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLORBTN => {
            let hdc = HDC(wparam.0 as *mut _);
            SetTextColor(hdc, COLORREF(TEXT_PRIMARY));
            SetBkMode(hdc, TRANSPARENT);
            if let Some(st) = UI_STATE.as_ref() {
                LRESULT(st.bg_brush.0 as isize)
            } else {
                LRESULT(0)
            }
        }
        WM_PAINT => {
            let mut ps = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut ps);
            if let Some(st) = UI_STATE.as_ref() {
                let mut rc = RECT::default();
                let _ = GetClientRect(hwnd, &mut rc);
                FillRect(hdc, &rc, st.bg_brush);

                let dpi = 96;
                let pad = scale_px(24, dpi);
                let w = rc.right - rc.left;

                // Card rectangle
                let card = RECT {
                    left: pad,
                    top: scale_px(66, dpi),
                    right: w - pad,
                    bottom: scale_px(176, dpi),
                };
                let border_br = CreateSolidBrush(COLORREF(BORDER_COLOR));
                FillRect(hdc, &card, border_br);
                let _ = DeleteObject(border_br.into());
                let inner = RECT {
                    left: card.left + 1,
                    top: card.top + 1,
                    right: card.right - 1,
                    bottom: card.bottom - 1,
                };
                FillRect(hdc, &inner, st.card_brush);

                // Text painting
                unsafe fn draw_line(hdc: HDC, text: &str, mut rc: RECT, ellipsis: bool) {
                    let mut buf = to_wide(text);
                    let len = buf.len().saturating_sub(1);
                    let mut flags = windows::Win32::Graphics::Gdi::DT_LEFT
                        | windows::Win32::Graphics::Gdi::DT_SINGLELINE;
                    if ellipsis {
                        flags |= windows::Win32::Graphics::Gdi::DT_END_ELLIPSIS;
                    }
                    let _ = windows::Win32::Graphics::Gdi::DrawTextW(
                        hdc,
                        &mut buf[..len],
                        &mut rc,
                        flags,
                    );
                }

                SetBkMode(hdc, TRANSPARENT);
                let prev_font = windows::Win32::Graphics::Gdi::SelectObject(hdc, st.font_title.into());
                SetTextColor(hdc, COLORREF(TEXT_PRIMARY));
                draw_line(
                    hdc,
                    "instant replay",
                    RECT {
                        left: pad,
                        top: scale_px(20, dpi),
                        right: w - pad,
                        bottom: scale_px(46, dpi),
                    },
                    false,
                );

                let _ = windows::Win32::Graphics::Gdi::SelectObject(hdc, st.font_small.into());
                SetTextColor(hdc, COLORREF(TEXT_SECONDARY));
                let sub_str = format!("version {APP_VERSION} · desktop replay buffer");
                draw_line(
                    hdc,
                    &sub_str,
                    RECT {
                        left: pad,
                        top: scale_px(44, dpi),
                        right: w - pad,
                        bottom: scale_px(62, dpi),
                    },
                    false,
                );

                // Status text inside card
                let _ = windows::Win32::Graphics::Gdi::SelectObject(hdc, st.font_body.into());
                SetTextColor(hdc, COLORREF(TEXT_PRIMARY));
                draw_line(
                    hdc,
                    &st.status_text,
                    RECT {
                        left: pad + scale_px(16, dpi),
                        top: scale_px(82, dpi),
                        right: w - pad - scale_px(16, dpi),
                        bottom: scale_px(106, dpi),
                    },
                    true,
                );

                // Progress bar inside card
                let bar_rc = RECT {
                    left: pad + scale_px(16, dpi),
                    top: scale_px(114, dpi),
                    right: w - pad - scale_px(16, dpi),
                    bottom: scale_px(122, dpi),
                };
                let track_br = CreateSolidBrush(COLORREF(TRACK_COLOR));
                FillRect(hdc, &bar_rc, track_br);
                let _ = DeleteObject(track_br.into());
                let bar_w = bar_rc.right - bar_rc.left;
                let fill_w = (bar_w * st.progress_pct as i32) / 100;
                if fill_w > 0 {
                    let fill_rc = RECT {
                        left: bar_rc.left,
                        top: bar_rc.top,
                        right: bar_rc.left + fill_w,
                        bottom: bar_rc.bottom,
                    };
                    let fill_br = CreateSolidBrush(COLORREF(FILL_COLOR));
                    FillRect(hdc, &fill_rc, fill_br);
                    let _ = DeleteObject(fill_br.into());
                }

                // Detail text inside card
                let _ = windows::Win32::Graphics::Gdi::SelectObject(hdc, st.font_small.into());
                SetTextColor(hdc, COLORREF(TEXT_SECONDARY));
                draw_line(
                    hdc,
                    &st.detail_text,
                    RECT {
                        left: pad + scale_px(16, dpi),
                        top: scale_px(136, dpi),
                        right: w - pad - scale_px(16, dpi),
                        bottom: scale_px(160, dpi),
                    },
                    true,
                );

                let _ = windows::Win32::Graphics::Gdi::SelectObject(hdc, prev_font);
            }
            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        WM_SETUP_PROGRESS => {
            if let Some(st) = UI_STATE.as_mut() {
                st.progress_pct = (wparam.0 as u32).min(100);
                if let Some(s) = NEXT_MSG_STATUS.take() {
                    st.status_text = s;
                }
                let _ = InvalidateRect(Some(hwnd), None, false);
            }
            LRESULT(0)
        }
        WM_SETUP_DONE => {
            if let Some(st) = UI_STATE.as_mut() {
                st.installing = false;
                st.finished = true;
                st.progress_pct = 100;
                st.status_text = "installation complete".to_string();
                if st.update_mode || st.launch_after {
                    launch_installed_app();
                    if st.update_mode {
                        PostQuitMessage(0);
                        return LRESULT(0);
                    }
                }
                let txt = to_wide("finish");
                let _ = SetWindowTextW(st.btn_primary, PCWSTR(txt.as_ptr()));
                let _ = EnableWindow(st.btn_primary, true);
                let _ = EnableWindow(st.btn_cancel, false);
                let _ = InvalidateRect(Some(hwnd), None, false);
            }
            LRESULT(0)
        }
        WM_SETUP_ERROR => {
            if let Some(st) = UI_STATE.as_mut() {
                st.installing = false;
                let err = NEXT_MSG_STATUS.take().unwrap_or_else(|| "installation failed".to_string());
                st.status_text = err.clone();
                let _ = EnableWindow(st.btn_primary, true);
                let _ = InvalidateRect(Some(hwnd), None, false);
                let title = to_wide("instant replay setup");
                let msg_w = to_wide(&err);
                MessageBoxW(Some(hwnd), PCWSTR(msg_w.as_ptr()), PCWSTR(title.as_ptr()), MB_OK | MB_ICONERROR);
            }
            LRESULT(0)
        }
        WM_COMMAND => {
            let id = wparam.0 & 0xFFFF;
            if id == ID_BTN_PRIMARY {
                let finished = UI_STATE.as_ref().map(|s| s.finished).unwrap_or(false);
                if finished {
                    PostQuitMessage(0);
                } else {
                    start_install_worker(hwnd);
                }
                return LRESULT(0);
            } else if id == ID_BTN_CANCEL {
                INSTALL_ABORTED.store(true, Ordering::Relaxed);
                let _ = DestroyWindow(hwnd);
                return LRESULT(0);
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        WM_CLOSE => {
            INSTALL_ABORTED.store(true, Ordering::Relaxed);
            let _ = DestroyWindow(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            if let Some(st) = UI_STATE.take() {
                let _ = DeleteObject(st.bg_brush.into());
                let _ = DeleteObject(st.card_brush.into());
                let _ = DeleteObject(st.font_title.into());
                let _ = DeleteObject(st.font_body.into());
                let _ = DeleteObject(st.font_small.into());
            }
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn make_font(height: i32, weight: i32) -> HFONT {
    let face = to_wide("Segoe UI");
    unsafe {
        let f = CreateFontW(
            height,
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            windows::Win32::Graphics::Gdi::DEFAULT_CHARSET,
            windows::Win32::Graphics::Gdi::OUT_DEFAULT_PRECIS,
            windows::Win32::Graphics::Gdi::CLIP_DEFAULT_PRECIS,
            windows::Win32::Graphics::Gdi::CLEARTYPE_QUALITY,
            0,
            PCWSTR(face.as_ptr()),
        );
        if f.is_invalid() {
            HFONT(GetStockObject(DEFAULT_GUI_FONT).0)
        } else {
            f
        }
    }
}

fn main() {
    let args: Vec<String> = env::args().skip(1).collect();
    let silent_flag = args.iter().any(|a| {
        let l = a.to_ascii_lowercase();
        l == "/silent" || l == "--silent"
    });
    if args.iter().any(|a| {
        let l = a.to_ascii_lowercase();
        l == "--uninstall" || l == "/uninstall"
    }) {
        run_uninstall(silent_flag);
        return;
    }

    let update_mode = silent_flag || args.iter().any(|a| {
        let l = a.to_ascii_lowercase();
        l == "--update" || l == "/update"
    });

    let dir = install_dir();
    let is_upgrade = dir.join("replay.exe").exists();
    let dn8 = find_installed_dotnet8();
    let initial_status = if update_mode {
        "updating instant replay...".to_string()
    } else if is_upgrade {
        "ready to update existing installation".to_string()
    } else {
        "ready to install instant replay".to_string()
    };
    let initial_detail = match dn8 {
        Some((a, b, c)) => format!(".net desktop {a}.{b}.{c} detected · {}", dir.display()),
        None => format!(".net 8 desktop runtime will be installed · {}", dir.display()),
    };

    unsafe {
        let hinst: HINSTANCE = GetModuleHandleW(None).unwrap().into();
        let cls_name = to_wide("InstantReplaySetupWnd");
        let icon: HICON = LoadIconW(Some(hinst), PCWSTR(1 as *const u16)).unwrap_or_default();

        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            hInstance: hinst,
            hIcon: icon,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: CreateSolidBrush(COLORREF(BG_COLOR)),
            lpszClassName: PCWSTR(cls_name.as_ptr()),
            ..Default::default()
        };
        RegisterClassW(&wc);

        let w = 480;
        let h = 290;
        let sx = GetSystemMetrics(SM_CXSCREEN);
        let sy = GetSystemMetrics(SM_CYSCREEN);
        let x = ((sx - w) / 2).max(0);
        let y = ((sy - h) / 2).max(0);

        let title = to_wide("instant replay setup");
        let hwnd = CreateWindowExW(
            Default::default(),
            PCWSTR(cls_name.as_ptr()),
            PCWSTR(title.as_ptr()),
            WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN,
            x,
            y,
            w,
            h,
            None,
            None,
            Some(hinst),
            None,
        )
        .unwrap();

        let mut rc = RECT::default();
        let _ = GetClientRect(hwnd, &mut rc);
        let cw = rc.right - rc.left;
        let ch = rc.bottom - rc.top;

        let font_title = make_font(-20, 600);
        let font_body = make_font(-14, 400);
        let font_small = make_font(-12, 400);

        let btn_cls = to_wide("BUTTON");
        let chk_desk_txt = to_wide("create desktop shortcut");
        let chk_desktop = CreateWindowExW(
            Default::default(),
            PCWSTR(btn_cls.as_ptr()),
            PCWSTR(chk_desk_txt.as_ptr()),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
            24,
            190,
            200,
            22,
            Some(hwnd),
            Some(windows::Win32::UI::WindowsAndMessaging::HMENU(ID_CHK_DESKTOP as *mut _)),
            Some(hinst),
            None,
        )
        .unwrap();
        set_ctrl_font(chk_desktop, font_small);
        if !is_upgrade && !update_mode {
            let _ = SendMessageW(chk_desktop, BM_SETCHECK, Some(WPARAM(BST_CHECKED.0 as usize)), None);
        }

        let chk_launch_txt = to_wide("launch after install");
        let chk_launch = CreateWindowExW(
            Default::default(),
            PCWSTR(btn_cls.as_ptr()),
            PCWSTR(chk_launch_txt.as_ptr()),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
            240,
            190,
            200,
            22,
            Some(hwnd),
            Some(windows::Win32::UI::WindowsAndMessaging::HMENU(ID_CHK_LAUNCH as *mut _)),
            Some(hinst),
            None,
        )
        .unwrap();
        set_ctrl_font(chk_launch, font_small);
        let _ = SendMessageW(chk_launch, BM_SETCHECK, Some(WPARAM(BST_CHECKED.0 as usize)), None);

        let primary_label = if update_mode { "updating..." } else if is_upgrade { "update" } else { "install" };
        let btn_prim_txt = to_wide(primary_label);
        let btn_primary = CreateWindowExW(
            Default::default(),
            PCWSTR(btn_cls.as_ptr()),
            PCWSTR(btn_prim_txt.as_ptr()),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE(BS_DEFPUSHBUTTON as u32),
            cw - 24 - 110,
            ch - 18 - 32,
            110,
            32,
            Some(hwnd),
            Some(windows::Win32::UI::WindowsAndMessaging::HMENU(ID_BTN_PRIMARY as *mut _)),
            Some(hinst),
            None,
        )
        .unwrap();
        set_ctrl_font(btn_primary, font_body);

        let btn_can_txt = to_wide("cancel");
        let btn_cancel = CreateWindowExW(
            Default::default(),
            PCWSTR(btn_cls.as_ptr()),
            PCWSTR(btn_can_txt.as_ptr()),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | windows::Win32::UI::WindowsAndMessaging::WINDOW_STYLE(BS_PUSHBUTTON as u32),
            cw - 24 - 110 - 10 - 92,
            ch - 18 - 32,
            92,
            32,
            Some(hwnd),
            Some(windows::Win32::UI::WindowsAndMessaging::HMENU(ID_BTN_CANCEL as *mut _)),
            Some(hinst),
            None,
        )
        .unwrap();
        set_ctrl_font(btn_cancel, font_body);

        UI_STATE = Some(UiState {
            hwnd,
            btn_primary,
            btn_cancel,
            chk_desktop,
            chk_launch,
            font_title,
            font_body,
            font_small,
            bg_brush: CreateSolidBrush(COLORREF(BG_COLOR)),
            card_brush: CreateSolidBrush(COLORREF(CARD_COLOR)),
            status_text: initial_status,
            detail_text: initial_detail,
            progress_pct: 0,
            installing: false,
            finished: false,
            update_mode,
            create_desktop_shortcut: !is_upgrade && !update_mode,
            launch_after: true,
            is_upgrade,
        });

        let _ = ShowWindow(hwnd, SW_SHOW);

        if update_mode {
            start_install_worker(hwnd);
        }

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).into() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}
