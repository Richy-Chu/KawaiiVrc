use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use crate::session;

/// One recorded presence event. Numbers follow the WPF app's ActivityType
/// enum so both histories can be rendered through the same labels.
#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct LoggedEvent {
    pub id: String,
    pub kind: u32,
    pub user_id: String,
    pub display_name: String,
    pub avatar: String,
    pub location: String,
    pub timestamp: String,
    pub details: String,
}

/// Append-only store for events the shell itself observes. It never writes the
/// WPF app's `activity.json`; that file stays read-only here so the two apps
/// cannot overwrite each other's history.
pub struct ActivityLog {
    path: PathBuf,
    gate: Mutex<()>,
}

impl ActivityLog {
    pub fn new() -> Self {
        Self {
            path: session::shell_dir().join("activity-events.jsonl"),
            gate: Mutex::new(()),
        }
    }

    pub fn record(&self, event: &LoggedEvent) {
        let Ok(line) = serde_json::to_string(event) else {
            return;
        };
        let Ok(_guard) = self.gate.lock() else {
            return;
        };
        if let Ok(mut file) = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)
        {
            let _ = writeln!(file, "{line}");
        }
    }

    pub fn count(&self) -> usize {
        self.load(usize::MAX).len()
    }

    /// Newest first, skipping lines a previous crash left half written.
    pub fn load(&self, limit: usize) -> Vec<LoggedEvent> {
        let Ok(raw) = fs::read_to_string(&self.path) else {
            return Vec::new();
        };
        let mut events: Vec<LoggedEvent> = raw
            .lines()
            .filter_map(|line| serde_json::from_str::<LoggedEvent>(line).ok())
            .collect();
        events.sort_by(|left, right| right.timestamp.cmp(&left.timestamp));
        events.truncate(limit);
        events
    }
}

pub fn now_iso() -> String {
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as i64)
        .unwrap_or(0);
    iso_from_millis(millis)
}

/// RFC 3339 in UTC with millisecond precision, without pulling in a date crate.
pub fn iso_from_millis(millis: i64) -> String {
    let seconds = millis.div_euclid(1000);
    let remainder = millis.rem_euclid(1000);
    let days = seconds.div_euclid(86_400);
    let seconds_of_day = seconds.rem_euclid(86_400);
    let (year, month, day) = civil_from_days(days);
    let hour = seconds_of_day / 3_600;
    let minute = (seconds_of_day % 3_600) / 60;
    let second = seconds_of_day % 60;
    format!(
        "{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{remainder:03}Z"
    )
}

fn civil_from_days(days: i64) -> (i64, u32, u32) {
    // Howard Hinnant's days-to-civil conversion.
    let shifted = days + 719_468;
    let era = if shifted >= 0 {
        shifted
    } else {
        shifted - 146_096
    } / 146_097;
    let day_of_era = shifted - era * 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_index = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * month_index + 2) / 5 + 1;
    let month = month_index + if month_index < 10 { 3 } else { -9 };
    (year + i64::from(month <= 2), month as u32, day as u32)
}

#[derive(Debug, PartialEq)]
pub struct MergeOutcome {
    pub imported: usize,
    pub skipped: usize,
    pub total: usize,
}

/// One-way import of shell events into the shared history the WPF app owns.
/// It is deliberately not a live merge: the WPF app caches that file in memory
/// and rewrites it wholesale, so a concurrent writer would lose data. The caller
/// must make sure the other app is closed before calling this.
/// One-way import into the account with the most history. The file is read and
/// parsed once: the account is chosen from the entries already in it instead of
/// by a separate feed query.
pub fn merge_into_latest_account(
    shared: &std::path::Path,
    events: &[LoggedEvent],
) -> Result<(String, MergeOutcome), String> {
    let mut root = read_shared(shared)?;
    let (account_id, outcome) = {
        let accounts = accounts_mut(&mut root)?;
        let account_id = accounts
            .iter()
            .max_by_key(|(_, value)| {
                value
                    .get("entries")
                    .and_then(serde_json::Value::as_array)
                    .map(Vec::len)
                    .unwrap_or(0)
            })
            .map(|(key, _)| key.clone())
            .ok_or_else(|| "No activity history yet.".to_string())?;
        let account = accounts.get_mut(&account_id).expect("chosen account exists");
        let outcome = merge_entries(entries_mut(account)?, events);
        (account_id, outcome)
    };
    write_shared(shared, &root)?;
    Ok((account_id, outcome))
}

fn read_shared(path: &std::path::Path) -> Result<serde_json::Value, String> {
    let raw = fs::read_to_string(path).map_err(|error| format!("{}: {error}", path.display()))?;
    serde_json::from_str(&raw)
        .map_err(|error| format!("The shared activity file is not valid JSON: {error}"))
}

fn accounts_mut(
    root: &mut serde_json::Value,
) -> Result<&mut serde_json::Map<String, serde_json::Value>, String> {
    root.get_mut("accounts")
        .and_then(serde_json::Value::as_object_mut)
        .ok_or_else(|| "The shared activity file has no accounts.".to_string())
}

fn entries_mut(
    account: &mut serde_json::Value,
) -> Result<&mut Vec<serde_json::Value>, String> {
    account
        .get_mut("entries")
        .and_then(serde_json::Value::as_array_mut)
        .ok_or_else(|| "That account has no activity entries.".to_string())
}

fn merge_entries(entries: &mut Vec<serde_json::Value>, events: &[LoggedEvent]) -> MergeOutcome {
    let mut known: HashSet<String> = entries
        .iter()
        .filter_map(|entry| entry.get("id").and_then(|value| value.as_str()))
        .map(str::to_string)
        .collect();

    let mut imported = 0usize;
    let mut skipped = 0usize;
    let mut additions = Vec::new();
    for event in events {
        if !known.insert(event.id.clone()) {
            skipped += 1;
            continue;
        }
        imported += 1;
        additions.push(serde_json::json!({
            "id": event.id,
            "type": event.kind,
            "userId": event.user_id,
            "userName": "",
            "displayName": event.display_name,
            "isSelf": false,
            "avatarUrl": event.avatar,
            "location": event.location,
            "timestamp": event.timestamp,
            "details": event.details,
            "isOnline": event.kind == 0,
        }));
    }

    additions.sort_by(|left, right| {
        let left = left.get("timestamp").and_then(|value| value.as_str()).unwrap_or("");
        let right = right.get("timestamp").and_then(|value| value.as_str()).unwrap_or("");
        right.cmp(left)
    });
    for addition in additions.into_iter().rev() {
        entries.insert(0, addition);
    }
    if entries.len() > 20_000 {
        entries.truncate(20_000);
    }

    MergeOutcome {
        imported,
        skipped,
        total: entries.len(),
    }
}

fn write_shared(path: &std::path::Path, root: &serde_json::Value) -> Result<(), String> {
    let temp = path.with_extension("json.shell-tmp");
    let bytes = serde_json::to_vec(root).map_err(|error| error.to_string())?;
    fs::write(&temp, bytes).map_err(|error| format!("Could not write the merged history: {error}"))?;
    fs::rename(&temp, path)
        .map_err(|error| format!("Could not replace the shared history: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_epoch_and_a_known_timestamp() {
        assert_eq!(iso_from_millis(0), "1970-01-01T00:00:00.000Z");
        // 1_700_000_000_000 ms is 2023-11-14T22:13:20Z.
        assert_eq!(
            iso_from_millis(1_700_000_000_000),
            "2023-11-14T22:13:20.000Z"
        );
    }

    #[test]
    fn timestamps_sort_chronologically_as_strings() {
        let earlier = iso_from_millis(1_700_000_000_000);
        let later = iso_from_millis(1_700_000_060_000);
        assert!(earlier < later);
    }

    #[test]
    fn records_and_reads_events_newest_first() {
        let directory = std::env::temp_dir().join(format!(
            "kawaii-shell-activity-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::create_dir_all(&directory);
        let path = directory.join("activity-events.jsonl");
        let log = ActivityLog {
            path: path.clone(),
            gate: Mutex::new(()),
        };

        for (index, kind) in [0u32, 1, 13].into_iter().enumerate() {
            log.record(&LoggedEvent {
                id: format!("event-{index}"),
                kind,
                user_id: "usr_test".to_string(),
                display_name: "Test".to_string(),
                avatar: String::new(),
                location: "offline".to_string(),
                timestamp: iso_from_millis(1_700_000_000_000 + index as i64 * 1000),
                details: String::new(),
            });
        }

        let loaded = log.load(10);
        assert_eq!(loaded.len(), 3);
        assert_eq!(loaded[0].id, "event-2");
        assert_eq!(loaded[2].id, "event-0");
        assert_eq!(log.count(), 3);
        let _ = std::fs::remove_file(path);
        let _ = std::fs::remove_dir(directory);
    }
}

#[cfg(test)]
mod merge_tests {
    use super::*;

    fn event(id: &str, kind: u32, timestamp: &str) -> LoggedEvent {
        LoggedEvent {
            id: id.to_string(),
            kind,
            user_id: "usr_friend".to_string(),
            display_name: "Friend".to_string(),
            avatar: String::new(),
            location: "offline".to_string(),
            timestamp: timestamp.to_string(),
            details: "went offline".to_string(),
        }
    }

    fn shared_file(directory: &std::path::Path) -> std::path::PathBuf {
        let path = directory.join("activity.json");
        let existing = serde_json::json!({
            "accounts": {
                "usr_self": {
                    "entries": [
                        { "id": "existing-1", "type": 0, "userId": "usr_friend", "displayName": "Friend",
                          "timestamp": "2026-11-19T10:00:00.000Z", "details": "came online" },
                        { "id": "existing-2", "type": 1, "userId": "usr_other", "displayName": "Other",
                          "timestamp": "2026-11-18T10:00:00.000Z", "details": "went offline" }
                    ]
                }
            }
        });
        std::fs::write(&path, serde_json::to_vec(&existing).expect("fixture")).expect("write");
        path
    }

    #[test]
    fn imports_only_new_events_and_keeps_newest_first() {
        let directory = std::env::temp_dir().join(format!(
            "kawaii-shell-merge-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::create_dir_all(&directory);
        let path = shared_file(&directory);

        let events = vec![
            event("existing-1", 0, "2026-11-19T10:00:00.000Z"),
            event("new-1", 1, "2026-11-19T12:00:00.000Z"),
            event("new-2", 13, "2026-11-19T11:00:00.000Z"),
        ];

        let (_, outcome) = merge_into_latest_account(&path, &events).expect("merge");
        assert_eq!(outcome.imported, 2);
        assert_eq!(outcome.skipped, 1);
        assert_eq!(outcome.total, 4);

        let written: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&path).expect("read")).expect("json");
        let entries = written["accounts"]["usr_self"]["entries"]
            .as_array()
            .expect("entries");
        assert_eq!(entries.len(), 4);
        assert_eq!(entries[0]["id"], "new-1");
        assert_eq!(entries[1]["id"], "new-2");
        assert_eq!(entries[2]["id"], "existing-1");
        assert_eq!(entries[0]["type"], 1);
        assert_eq!(entries[0]["isOnline"], false);

        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn refuses_a_file_without_accounts() {
        let directory = std::env::temp_dir().join(format!(
            "kawaii-shell-merge-unknown-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::create_dir_all(&directory);
        let path = directory.join("activity.json");
        std::fs::write(&path, "{\"accounts\":{}}").expect("write");

        let error = merge_into_latest_account(&path, &[]).expect_err("must fail");
        assert!(error.contains("No activity history"));
        let _ = std::fs::remove_dir_all(directory);
    }

    #[test]
    fn a_second_merge_changes_nothing() {
        let directory = std::env::temp_dir().join(format!(
            "kawaii-shell-merge-twice-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::create_dir_all(&directory);
        let path = shared_file(&directory);
        let events = vec![event("new-1", 1, "2026-11-19T12:00:00.000Z")];

        merge_into_latest_account(&path, &events).expect("first");
        let (_, second) = merge_into_latest_account(&path, &events).expect("second");
        assert_eq!(second.imported, 0);
        assert_eq!(second.skipped, 1);
        assert_eq!(second.total, 3);
        let _ = std::fs::remove_dir_all(directory);
    }
}

#[cfg(test)]
mod latest_account_tests {
    use super::*;

    fn event(id: &str) -> LoggedEvent {
        LoggedEvent {
            id: id.to_string(),
            kind: 1,
            user_id: "usr_friend".to_string(),
            display_name: "Friend".to_string(),
            avatar: String::new(),
            location: "offline".to_string(),
            timestamp: "2026-11-19T12:00:00.000Z".to_string(),
            details: "went offline".to_string(),
        }
    }

    #[test]
    fn picks_the_account_with_the_most_history_in_one_read() {
        let directory = std::env::temp_dir().join(format!(
            "kawaii-shell-latest-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::create_dir_all(&directory);
        let path = directory.join("activity.json");
        let existing = serde_json::json!({
            "accounts": {
                "usr_small": { "entries": [ { "id": "small-1", "type": 0, "timestamp": "2026-11-01T10:00:00.000Z" } ] },
                "usr_big": { "entries": [
                    { "id": "big-1", "type": 0, "timestamp": "2026-11-02T10:00:00.000Z" },
                    { "id": "big-2", "type": 1, "timestamp": "2026-11-03T10:00:00.000Z" }
                ] }
            }
        });
        std::fs::write(&path, serde_json::to_vec(&existing).expect("fixture")).expect("write");

        let (account, outcome) =
            merge_into_latest_account(&path, &[event("new-1")]).expect("merge");
        assert_eq!(account, "usr_big");
        assert_eq!(outcome.imported, 1);
        assert_eq!(outcome.total, 3);

        let written: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&path).expect("read")).expect("json");
        assert_eq!(written["accounts"]["usr_small"]["entries"].as_array().unwrap().len(), 1);
        assert_eq!(written["accounts"]["usr_big"]["entries"][0]["id"], "new-1");

        let _ = std::fs::remove_dir_all(directory);
    }
}
