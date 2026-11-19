use serde::{Deserialize, Serialize};
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
