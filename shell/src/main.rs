#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::Serialize;
use serde_json::Value;
use std::fs;
use std::path::PathBuf;

const KINDS: [&str; 14] = [
    "online",
    "offline",
    "joined world",
    "left world",
    "joined instance",
    "left instance",
    "friend request",
    "group invite",
    "world created",
    "profile updated",
    "friend added",
    "friend removed",
    "bio changed",
    "avatar changed",
];

fn data_dir() -> PathBuf {
    let base = std::env::var("LOCALAPPDATA").unwrap_or_default();
    PathBuf::from(base).join("VrcCompanion")
}

fn text(value: &Value, key: &str) -> String {
    value
        .get(key)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string()
}

#[derive(Serialize)]
struct Summary {
    activity_bytes: u64,
    history_bytes: u64,
    media_bytes: u64,
    friend_bytes: u64,
    accounts: usize,
}

/// One line per command call, so a run can be checked without a console window.
fn trace(command: &str) {
    use std::io::Write;
    if let Ok(mut file) = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open("shell-ui.log")
    {
        let _ = writeln!(file, "{command}");
    }
}

#[tauri::command]
fn summary() -> Summary {
    trace("summary");
    let dir = data_dir();
    let size = |name: &str| fs::metadata(dir.join(name)).map(|meta| meta.len()).unwrap_or(0);
    let accounts = fs::read_to_string(dir.join("activity.json"))
        .ok()
        .and_then(|raw| serde_json::from_str::<Value>(&raw).ok())
        .and_then(|root| {
            root.get("accounts")
                .and_then(Value::as_object)
                .map(|accounts| accounts.len())
        })
        .unwrap_or(0);

    Summary {
        activity_bytes: size("activity.json"),
        history_bytes: size("instance-history.db"),
        media_bytes: size("media-index.json"),
        friend_bytes: size("friend-avatars.json"),
        accounts,
    }
}

#[derive(Serialize)]
struct ActivityItem {
    id: String,
    kind: String,
    name: String,
    details: String,
    at: String,
    avatar: String,
    location: String,
}

#[derive(Serialize)]
struct Feed {
    account: String,
    total: usize,
    items: Vec<ActivityItem>,
}

#[tauri::command]
fn feed(account: Option<String>, limit: Option<usize>) -> Result<Feed, String> {
    trace("feed");
    let path = data_dir().join("activity.json");
    let raw = fs::read_to_string(&path).map_err(|error| format!("{}: {error}", path.display()))?;
    let root: Value = serde_json::from_str(&raw).map_err(|error| error.to_string())?;

    let empty = serde_json::Map::new();
    let accounts = root
        .get("accounts")
        .and_then(Value::as_object)
        .unwrap_or(&empty);

    let chosen = match account {
        Some(wanted) => accounts.get(&wanted).map(|value| (wanted, value)),
        None => accounts
            .iter()
            .max_by_key(|(_, value)| {
                value
                    .get("entries")
                    .and_then(Value::as_array)
                    .map(Vec::len)
                    .unwrap_or(0)
            })
            .map(|(key, value)| (key.clone(), value)),
    };

    let (id, value) = chosen.ok_or_else(|| "No activity history yet.".to_string())?;
    let entries = value
        .get("entries")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let limit = limit.unwrap_or(200).clamp(1, 1000);

    let items = entries
        .iter()
        .take(limit)
        .map(|entry| {
            let display = text(entry, "displayName");
            ActivityItem {
                id: text(entry, "id"),
                kind: entry
                    .get("type")
                    .and_then(Value::as_u64)
                    .and_then(|kind| KINDS.get(kind as usize))
                    .copied()
                    .unwrap_or("event")
                    .to_string(),
                name: if display.is_empty() {
                    text(entry, "userName")
                } else {
                    display
                },
                details: {
                    let details = text(entry, "details");
                    if details.is_empty() {
                        text(entry, "location")
                    } else {
                        details
                    }
                },
                at: {
                    let timestamp = text(entry, "timestamp");
                    if timestamp.is_empty() {
                        text(entry, "at")
                    } else {
                        timestamp
                    }
                },
                avatar: text(entry, "avatarUrl"),
                location: text(entry, "location"),
            }
        })
        .collect();

    Ok(Feed {
        account: id,
        total: entries.len(),
        items,
    })
}

#[derive(Serialize)]
struct InstanceRow {
    world: String,
    world_id: String,
    location: String,
    first_seen: String,
    last_seen: String,
}

#[tauri::command]
fn instances(limit: Option<usize>) -> Result<Vec<InstanceRow>, String> {
    trace("instances");
    let path = data_dir().join("instance-history.db");
    if !path.exists() {
        return Ok(Vec::new());
    }

    let connection =
        rusqlite::Connection::open_with_flags(&path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(|error| error.to_string())?;
    let limit = limit.unwrap_or(60).clamp(1, 500);
    let mut statement = connection
        .prepare(
            "SELECT world_name, world_id, location, first_seen_at, last_updated_at
             FROM gamelog_location
             ORDER BY last_updated_at DESC
             LIMIT ?1",
        )
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map([limit as i64], |row| {
            Ok(InstanceRow {
                world: row.get(0)?,
                world_id: row.get(1)?,
                location: row.get(2)?,
                first_seen: row.get(3)?,
                last_seen: row.get(4)?,
            })
        })
        .map_err(|error| error.to_string())?;

    let mut out = Vec::new();
    for row in rows {
        out.push(row.map_err(|error| error.to_string())?);
    }
    Ok(out)
}

/// Console output is invisible in a windowed release build, so `--dump` writes
/// a small report next to the executable instead. It exercises the same command
/// functions the UI calls.
fn dump_report() {
    let mut out = String::new();
    let summary = summary();
    out.push_str(&format!(
        "accounts={} activity={}MB history={}MB media={}MB avatars={}MB\n",
        summary.accounts,
        summary.activity_bytes / (1024 * 1024),
        summary.history_bytes / (1024 * 1024),
        summary.media_bytes / (1024 * 1024),
        summary.friend_bytes / (1024 * 1024)
    ));

    match feed(None, Some(3)) {
        Ok(result) => {
            out.push_str(&format!(
                "feed account={} total={} shown={}\n",
                result.account,
                result.total,
                result.items.len()
            ));
            for item in &result.items {
                out.push_str(&format!(
                    "  kind={} name={} at={} details={}\n",
                    item.kind, item.name, item.at, item.details
                ));
            }
        }
        Err(error) => out.push_str(&format!("feed error: {error}\n")),
    }

    match instances(Some(3)) {
        Ok(rows) => {
            out.push_str(&format!("instances shown={}\n", rows.len()));
            for row in &rows {
                out.push_str(&format!(
                    "  world={} last_seen={} location={}\n",
                    row.world, row.last_seen, row.location
                ));
            }
        }
        Err(error) => out.push_str(&format!("instances error: {error}\n")),
    }

    let _ = fs::write("shell-dump.txt", out);
}

fn main() {
    if std::env::args().any(|argument| argument == "--dump") {
        dump_report();
        return;
    }

    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![summary, feed, instances])
        .run(tauri::generate_context!())
        .expect("failed to start the KawaiiVrc shell");
}
