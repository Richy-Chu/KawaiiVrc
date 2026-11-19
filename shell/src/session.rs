use std::fs;
use std::path::PathBuf;

const SESSION_ENTROPY: &[u8] = b"VrcCompanion.Session.v1";
const ACCOUNT_ENTROPY: &[u8] = b"VrcCompanion.Account.v1";

pub fn local_app_data() -> PathBuf {
    PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_default())
}

pub fn shell_dir() -> PathBuf {
    let dir = local_app_data().join("KawaiiVrcShell");
    let _ = fs::create_dir_all(&dir);
    dir
}

#[cfg(windows)]
mod dpapi {
    use std::ffi::c_void;
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB,
    };

    fn blob(data: &[u8]) -> CRYPT_INTEGER_BLOB {
        CRYPT_INTEGER_BLOB {
            cbData: data.len() as u32,
            pbData: data.as_ptr() as *mut u8,
        }
    }

    unsafe fn take(blob: CRYPT_INTEGER_BLOB) -> Option<Vec<u8>> {
        if blob.pbData.is_null() {
            return None;
        }
        let bytes = std::slice::from_raw_parts(blob.pbData, blob.cbData as usize).to_vec();
        LocalFree(blob.pbData as *mut c_void);
        Some(bytes)
    }

    pub fn unprotect(cipher: &[u8], entropy: &[u8]) -> Option<Vec<u8>> {
        let input = blob(cipher);
        let extra = blob(entropy);
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: std::ptr::null_mut(),
        };
        let ok = unsafe {
            CryptUnprotectData(
                &input,
                std::ptr::null_mut(),
                &extra,
                std::ptr::null(),
                std::ptr::null(),
                0,
                &mut output,
            )
        };
        if ok == 0 {
            return None;
        }
        unsafe { take(output) }
    }

    pub fn protect(plain: &[u8], entropy: &[u8]) -> Option<Vec<u8>> {
        let input = blob(plain);
        let extra = blob(entropy);
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: std::ptr::null_mut(),
        };
        let ok = unsafe {
            CryptProtectData(
                &input,
                std::ptr::null(),
                &extra,
                std::ptr::null(),
                std::ptr::null(),
                0,
                &mut output,
            )
        };
        if ok == 0 {
            return None;
        }
        unsafe { take(output) }
    }
}

#[cfg(not(windows))]
mod dpapi {
    pub fn unprotect(_cipher: &[u8], _entropy: &[u8]) -> Option<Vec<u8>> {
        None
    }

    pub fn protect(_plain: &[u8], _entropy: &[u8]) -> Option<Vec<u8>> {
        None
    }
}

fn read_cookie_json(path: PathBuf, entropy: &[u8]) -> Option<String> {
    let encrypted = fs::read(path).ok()?;
    let plain = dpapi::unprotect(&encrypted, entropy)?;
    let json: serde_json::Value = serde_json::from_slice(&plain).ok()?;
    let cookie = json.get("AuthCookie")?.as_str()?.trim().to_string();
    if cookie.is_empty() {
        None
    } else {
        Some(cookie)
    }
}

/// The single-account session the WPF app keeps in `session.dat`.
pub fn companion_cookie() -> Option<String> {
    read_cookie_json(
        local_app_data().join("VrcCompanion").join("session.dat"),
        SESSION_ENTROPY,
    )
}

/// The active account in the WPF app's multi-account store, if any.
pub fn account_cookie() -> Option<String> {
    let accounts = local_app_data().join("VrcCompanion").join("accounts");
    let active = fs::read_to_string(accounts.join("active.json")).ok()?;
    let json: serde_json::Value = serde_json::from_str(&active).ok()?;
    let id = json.get("activeAccountId")?.as_str()?;
    let encrypted = fs::read(accounts.join(format!("{id}.cookie"))).ok()?;
    let plain = dpapi::unprotect(&encrypted, ACCOUNT_ENTROPY)?;
    let cookie = String::from_utf8(plain).ok()?.trim().to_string();
    if cookie.is_empty() {
        None
    } else {
        Some(cookie)
    }
}

pub fn load_shell_cookie() -> Option<String> {
    read_cookie_json(shell_dir().join("session.dat"), SESSION_ENTROPY)
}

pub fn save_shell_cookie(cookie: &str) {
    let payload = serde_json::json!({
        "AuthCookie": cookie,
        "SavedAt": chrono_like_now()
    });
    let Ok(plain) = serde_json::to_vec(&payload) else {
        return;
    };
    let Some(encrypted) = dpapi::protect(&plain, SESSION_ENTROPY) else {
        return;
    };
    let _ = fs::write(shell_dir().join("session.dat"), encrypted);
}

pub fn clear_shell_cookie() {
    let _ = fs::remove_file(shell_dir().join("session.dat"));
}

fn chrono_like_now() -> String {
    use std::time::{SystemTime, UNIX_EPOCH};
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs())
        .unwrap_or_default();
    format!("{seconds}")
}
