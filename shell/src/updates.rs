use serde::{Deserialize, Serialize};
use std::collections::HashMap;

/// The `latest.json` Tauri's updater reads from the release endpoint.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct Manifest {
    pub version: String,
    #[serde(default)]
    pub notes: String,
    #[serde(default)]
    pub pub_date: String,
    #[serde(default)]
    pub platforms: HashMap<String, Platform>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct Platform {
    pub signature: String,
    pub url: String,
}

pub fn parse_manifest(raw: &str) -> Result<Manifest, String> {
    let manifest: Manifest =
        serde_json::from_str(raw).map_err(|error| format!("bad update manifest: {error}"))?;
    if manifest.version.trim().is_empty() {
        return Err("The update manifest has no version.".to_string());
    }
    if manifest.platforms.is_empty() {
        return Err("The update manifest has no platforms.".to_string());
    }
    for (name, platform) in &manifest.platforms {
        if platform.signature.trim().is_empty() || platform.url.trim().is_empty() {
            return Err(format!("The update manifest entry for {name} is incomplete."));
        }
    }
    Ok(manifest)
}

pub fn windows_platform(manifest: &Manifest) -> Option<&Platform> {
    manifest.platforms.get("windows-x86_64")
}

/// Numeric segment comparison, tolerant of `v` prefixes and pre-release tails.
pub fn is_newer(current: &str, candidate: &str) -> bool {
    let parse = |value: &str| -> Vec<u64> {
        value
            .trim()
            .trim_start_matches('v')
            .split(['.', '-', '+'])
            .map(|part| part.parse::<u64>().unwrap_or(0))
            .collect()
    };

    let current = parse(current);
    let candidate = parse(candidate);
    for index in 0..current.len().max(candidate.len()) {
        let existing = *current.get(index).unwrap_or(&0);
        let offered = *candidate.get(index).unwrap_or(&0);
        if offered != existing {
            return offered > existing;
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = include_str!("../fixtures/latest.json");

    #[test]
    fn parses_the_release_manifest() {
        let manifest = parse_manifest(SAMPLE).expect("manifest");
        assert_eq!(manifest.version, "0.4.0");
        let windows = windows_platform(&manifest).expect("windows entry");
        assert!(windows.url.ends_with("KawaiiVrc%20Shell_0.4.0_x64-setup.exe"));
        assert!(windows.signature.starts_with("dW50cnVzdGVk"));
    }

    #[test]
    fn rejects_incomplete_manifests() {
        assert!(parse_manifest("{}").is_err());
        assert!(parse_manifest(r#"{"version":"0.4.0"}"#).is_err());
        assert!(parse_manifest(
            r#"{"version":"0.4.0","platforms":{"windows-x86_64":{"signature":"","url":"https://x"}}}"#
        )
        .is_err());
        assert!(parse_manifest("not json").is_err());
    }

    #[test]
    fn compares_versions() {
        assert!(is_newer("0.3.0", "0.4.0"));
        assert!(is_newer("0.3.0", "0.3.1"));
        assert!(is_newer("0.3.9", "0.4.0"));
        assert!(is_newer("v0.3.0", "0.3.1"));
        assert!(!is_newer("0.3.1", "0.3.0"));
        assert!(!is_newer("0.3.0", "0.3.0"));
        assert!(!is_newer("0.3.10", "0.3.9"));
    }
}
