use serde::{Deserialize, Serialize};
use std::fs;

use crate::session;

/// The shell's own preferences. The WPF app's `settings.json` is left alone:
/// both files can be read at any time, but only one process owns each.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct ShellSettings {
    #[serde(default = "default_refresh")]
    pub refresh_seconds: u64,
    #[serde(default = "default_true")]
    pub show_offline: bool,
    #[serde(default = "default_true")]
    pub animate_backdrop: bool,
}

fn default_refresh() -> u64 {
    60
}

fn default_true() -> bool {
    true
}

impl Default for ShellSettings {
    fn default() -> Self {
        Self {
            refresh_seconds: default_refresh(),
            show_offline: true,
            animate_backdrop: true,
        }
    }
}

pub fn path() -> std::path::PathBuf {
    session::shell_dir().join("settings.json")
}

pub fn load() -> ShellSettings {
    fs::read_to_string(path())
        .ok()
        .and_then(|raw| serde_json::from_str::<ShellSettings>(&raw).ok())
        .map(sanitize)
        .unwrap_or_default()
}

pub fn save(settings: &ShellSettings) -> Result<(), String> {
    let settings = sanitize(settings.clone());
    let raw = serde_json::to_string_pretty(&settings).map_err(|error| error.to_string())?;
    fs::write(path(), raw).map_err(|error| format!("Could not save settings: {error}"))
}

fn sanitize(mut settings: ShellSettings) -> ShellSettings {
    settings.refresh_seconds = settings.refresh_seconds.clamp(15, 900);
    settings
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clamps_the_refresh_interval() {
        let fast = sanitize(ShellSettings {
            refresh_seconds: 1,
            ..Default::default()
        });
        assert_eq!(fast.refresh_seconds, 15);

        let slow = sanitize(ShellSettings {
            refresh_seconds: 10_000,
            ..Default::default()
        });
        assert_eq!(slow.refresh_seconds, 900);
    }

    #[test]
    fn missing_fields_fall_back_to_defaults() {
        let parsed: ShellSettings = serde_json::from_str("{}").expect("defaults");
        assert_eq!(parsed, ShellSettings::default());
    }
}
