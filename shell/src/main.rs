#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod activity;
mod chatbox;
mod favorites;
mod session;
mod settings;
mod updates;
mod vrchat;

use serde::Serialize;
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;
use tauri::Manager;
use vrchat::{Api, InstanceView, LoginStart, Notice, Roster, UserInfo};

fn data_dir() -> PathBuf {
    session::local_app_data().join("VrcCompanion")
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
    logged_events: usize,
}

#[tauri::command]
fn summary() -> Summary {
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
        logged_events: activity::ActivityLog::new().count(),
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

fn kind_label(kind: u32) -> String {
    KINDS
        .get(kind as usize)
        .copied()
        .unwrap_or("event")
        .to_string()
}

/// The WPF history plus the events the shell observed itself, newest first.
#[tauri::command]
fn feed(account: Option<String>, limit: Option<usize>) -> Result<Feed, String> {
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

    let mut items: Vec<ActivityItem> = entries
        .iter()
        .map(|entry| {
            let display = text(entry, "displayName");
            ActivityItem {
                id: text(entry, "id"),
                kind: entry
                    .get("type")
                    .and_then(Value::as_u64)
                    .map(|kind| kind_label(kind as u32))
                    .unwrap_or_else(|| "event".to_string()),
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

    for event in activity::ActivityLog::new().load(500) {
        items.push(ActivityItem {
            id: event.id,
            kind: kind_label(event.kind),
            name: event.display_name,
            details: event.details,
            at: event.timestamp,
            avatar: event.avatar,
            location: event.location,
        });
    }

    items.sort_by(|left, right| right.at.cmp(&left.at));
    let mut seen = HashSet::new();
    items.retain(|item| seen.insert(item.id.clone()));
    items.truncate(limit.unwrap_or(200).clamp(1, 1000));

    Ok(Feed {
        account: id,
        total: entries.len() + activity::ActivityLog::new().count(),
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

// -------------------------------------------------------------------- media

#[derive(Serialize)]
struct MediaEntry {
    path: String,
    world: String,
    taken_at: String,
}

fn media_index() -> Option<Value> {
    let raw = fs::read_to_string(data_dir().join("media-index.json")).ok()?;
    serde_json::from_str(&raw).ok()
}

fn media_root() -> Option<String> {
    media_index()
        .and_then(|root| root.get("root").and_then(Value::as_str).map(str::to_string))
}

#[tauri::command]
fn media(limit: Option<usize>, offset: Option<usize>) -> Result<Vec<MediaEntry>, String> {
    let Some(root) = media_index() else {
        return Ok(Vec::new());
    };
    let entries = root
        .get("entries")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let offset = offset.unwrap_or(0);
    let limit = limit.unwrap_or(120).clamp(1, 500);

    Ok(entries
        .iter()
        .skip(offset)
        .take(limit)
        .map(|entry| MediaEntry {
            path: text(entry, "path"),
            world: text(entry, "world"),
            taken_at: text(entry, "takenAt"),
        })
        .collect())
}

/// Local pictures are served only when they sit inside the indexed media root.
fn local_media_path(path: &str) -> Option<PathBuf> {
    let root = media_root()?;
    let candidate = PathBuf::from(path);
    if !candidate.starts_with(Path::new(&root)) || !candidate.is_file() {
        return None;
    }
    Some(candidate)
}

fn mime_for(path: &Path) -> &'static str {
    match path
        .extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or_default()
        .to_lowercase()
        .as_str()
    {
        "jpg" | "jpeg" => "image/jpeg",
        "webp" => "image/webp",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        _ => "image/png",
    }
}

// ------------------------------------------------------------------ account

#[derive(Serialize)]
struct AuthState {
    signed_in: bool,
    live: bool,
    user: Option<UserInfo>,
}

#[tauri::command]
async fn auth_state(api: tauri::State<'_, Arc<Api>>) -> Result<AuthState, String> {
    let user = api.restore().await;
    if user.is_some() {
        let inner = api.inner().clone();
        inner.start_realtime();
    }
    Ok(AuthState {
        signed_in: user.is_some(),
        live: api.live(),
        user,
    })
}

#[tauri::command]
async fn sign_in(
    api: tauri::State<'_, Arc<Api>>,
    username: String,
    password: String,
) -> Result<LoginStart, String> {
    if username.trim().is_empty() || password.is_empty() {
        return Err("Enter your VRChat username and password.".to_string());
    }
    let result = api.login(username.trim(), &password).await?;
    if matches!(result, LoginStart::SignedIn { .. }) {
        let inner = api.inner().clone();
        inner.start_realtime();
    }
    Ok(result)
}

#[tauri::command]
async fn verify_two_factor(
    api: tauri::State<'_, Arc<Api>>,
    code: String,
    method: Option<String>,
) -> Result<UserInfo, String> {
    let code = code.trim();
    if code.is_empty() {
        return Err("Enter the code from your authenticator.".to_string());
    }
    let user = api
        .verify_two_factor(code, method.as_deref().unwrap_or("totp"))
        .await?;
    let inner = api.inner().clone();
    inner.start_realtime();
    Ok(user)
}

#[tauri::command]
async fn sign_out(api: tauri::State<'_, Arc<Api>>) -> Result<(), String> {
    api.sign_out().await;
    Ok(())
}

/// Hands the roster's unknown world ids to the background resolver.
fn start_world_lookup(api: &Arc<Api>, roster: &mut Roster) {
    let missing = std::mem::take(&mut roster.missing_worlds);
    api.resolve_worlds_in_background(missing);
}

#[tauri::command]
async fn friends(api: tauri::State<'_, Arc<Api>>) -> Result<Roster, String> {
    let mut roster = api.friends().await?;
    start_world_lookup(api.inner(), &mut roster);
    Ok(roster)
}

/// First page only: what the UI paints before the full roster finishes.
#[tauri::command]
async fn friends_quick(api: tauri::State<'_, Arc<Api>>) -> Result<Roster, String> {
    let mut roster = api.friends_quick().await?;
    start_world_lookup(api.inner(), &mut roster);
    api.inner().start_roster_refresh();
    Ok(roster)
}

/// The full roster once the background load has it; `null` while it is loading.
#[tauri::command]
fn friends_snapshot(api: tauri::State<'_, Arc<Api>>) -> Option<Roster> {
    api.roster_snapshot()
}

/// World names the roster could not show yet; the UI polls this while the
/// background lookup fills the cache.
#[tauri::command]
fn friend_worlds(api: tauri::State<'_, Arc<Api>>) -> HashMap<String, String> {
    api.world_names()
}

#[tauri::command]
async fn instance(api: tauri::State<'_, Arc<Api>>) -> Result<InstanceView, String> {
    api.instance().await
}

#[tauri::command]
async fn notifications(api: tauri::State<'_, Arc<Api>>) -> Result<Vec<Notice>, String> {
    api.notifications().await
}

// ----------------------------------------------------------------- settings

#[tauri::command]
fn settings_get() -> settings::ShellSettings {
    settings::load()
}

#[tauri::command]
fn settings_save(next: settings::ShellSettings) -> Result<(), String> {
    settings::save(&next)
}

// -------------------------------------------------------- updates + merging

#[derive(Serialize)]
struct UpdateStatus {
    available: bool,
    version: Option<String>,
    notes: Option<String>,
    message: String,
}

#[tauri::command]
async fn check_update(app: tauri::AppHandle) -> UpdateStatus {
    use tauri_plugin_updater::UpdaterExt;

    let idle = |message: String| UpdateStatus {
        available: false,
        version: None,
        notes: None,
        message,
    };

    let updater = match app.updater() {
        Ok(updater) => updater,
        Err(error) => return idle(format!("The updater is not configured: {error}")),
    };

    match updater.check().await {
        Ok(Some(update)) => UpdateStatus {
            available: true,
            version: Some(update.version.clone()),
            notes: update.body.clone(),
            message: format!("Version {} is available.", update.version),
        },
        Ok(None) => idle("You are running the newest shell build.".to_string()),
        Err(error) => idle(format!("Update check failed: {error}")),
    }
}

#[tauri::command]
async fn install_update(app: tauri::AppHandle) -> Result<(), String> {
    use tauri_plugin_updater::UpdaterExt;

    let updater = app.updater().map_err(|error| error.to_string())?;
    let update = updater
        .check()
        .await
        .map_err(|error| error.to_string())?
        .ok_or_else(|| "No update is available.".to_string())?;
    update
        .download_and_install(|_, _| {}, || {})
        .await
        .map_err(|error| error.to_string())?;
    app.restart();
}

// ----------------------------------------------------------------- favorites

#[tauri::command]
fn favorites() -> Result<favorites::Favorites, String> {
    let (avatar_ids, world_ids) = favorites::ids(&data_dir());
    Ok(favorites::view(
        &favorites::load(),
        &avatar_ids,
        &world_ids,
    ))
}

#[tauri::command]
async fn refresh_favorites(
    api: tauri::State<'_, Arc<Api>>,
    budget: Option<usize>,
) -> Result<favorites::Favorites, String> {
    let (avatar_ids, world_ids) = favorites::ids(&data_dir());
    let mut store = favorites::load();
    let (ask_avatars, ask_worlds) =
        favorites::pending(&store, &avatar_ids, &world_ids, budget.unwrap_or(2).clamp(1, 5));

    if !ask_avatars.is_empty() {
        let (found, rejected) = api.avatar_details(&ask_avatars).await;
        for (id, name, thumbnail) in found {
            favorites::apply(&mut store, "avatar", id, name, thumbnail);
        }
        for id in rejected {
            favorites::mark_missing(&mut store, "avatar", id);
        }
    }
    if !ask_worlds.is_empty() {
        let (found, rejected) = api.world_details(&ask_worlds).await;
        for (id, name, thumbnail) in found {
            favorites::apply(&mut store, "world", id, name, thumbnail);
        }
        for id in rejected {
            favorites::mark_missing(&mut store, "world", id);
        }
    }

    favorites::save(&store)?;
    Ok(favorites::view(&store, &avatar_ids, &world_ids))
}

#[tauri::command]
fn send_chatbox(message: String, send: Option<bool>) -> Result<String, String> {
    chatbox::send(&message, send.unwrap_or(true))?;
    Ok(format!(
        "Sent to VRChat's chatbox (OSC at {}).",
        chatbox::DEFAULT_ADDRESS
    ))
}

/// Validates a release manifest before it is uploaded.
#[tauri::command]
fn verify_manifest(path: Option<String>) -> Result<updates::Manifest, String> {
    let path = path
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("latest.json"));
    let raw = fs::read_to_string(&path).map_err(|error| format!("{}: {error}", path.display()))?;
    let manifest = updates::parse_manifest(&raw)?;
    if updates::windows_platform(&manifest).is_none() {
        return Err("The manifest has no windows-x86_64 entry.".to_string());
    }
    if !updates::is_newer(env!("CARGO_PKG_VERSION"), &manifest.version) {
        return Err(format!(
            "The manifest offers {}, which is not newer than {}.",
            manifest.version,
            env!("CARGO_PKG_VERSION")
        ));
    }
    Ok(manifest)
}

fn companion_running() -> bool {
    std::process::Command::new("tasklist")
        .args(["/FI", "IMAGENAME eq VrcCompanion.exe", "/NH"])
        .output()
        .map(|output| String::from_utf8_lossy(&output.stdout).contains("VrcCompanion.exe"))
        .unwrap_or(false)
}

/// One-way import of the shell's own events into the history the WPF app owns.
/// Only allowed while the other app is closed, because it rewrites that file.
#[tauri::command]
fn merge_activity() -> Result<String, String> {
    if companion_running() {
        return Err(
            "Close the other KawaiiVrc app first: it rewrites its activity file while running."
                .to_string(),
        );
    }

    let events = activity::ActivityLog::new().load(usize::MAX);
    if events.is_empty() {
        return Ok("No shell events to import yet.".to_string());
    }

    let path = data_dir().join("activity.json");
    if !path.exists() {
        return Err(format!("{} does not exist yet.", path.display()));
    }
    let (account, outcome) = activity::merge_into_latest_account(&path, &events)?;
    Ok(format!(
        "Imported {} new events into {account} ({} already there); the shared history now has {}.",
        outcome.imported, outcome.skipped, outcome.total
    ))
}

// -------------------------------------------------------------------- report

/// Console output is invisible in a windowed release build, so `--dump` writes
/// a small report next to the executable instead. It exercises the same command
/// functions the UI calls, and never prints a cookie or password.
fn dump_report() {
    let mut out = format!("version: {}\n", env!("CARGO_PKG_VERSION"));
    let summary = summary();
    out.push_str(&format!(
        "accounts={} activity={}MB history={}MB media={}MB avatars={}MB logged_events={}\n",
        summary.accounts,
        summary.activity_bytes / (1024 * 1024),
        summary.history_bytes / (1024 * 1024),
        summary.media_bytes / (1024 * 1024),
        summary.friend_bytes / (1024 * 1024),
        summary.logged_events
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
                    "  world={} last_seen={}\n",
                    row.world, row.last_seen
                ));
            }
        }
        Err(error) => out.push_str(&format!("instances error: {error}\n")),
    }

    match media(Some(3), Some(0)) {
        Ok(rows) => {
            out.push_str(&format!("media shown={}\n", rows.len()));
            for row in &rows {
                out.push_str(&format!(
                    "  {} | {} | {}\n",
                    row.world, row.taken_at, row.path
                ));
            }
        }
        Err(error) => out.push_str(&format!("media error: {error}\n")),
    }

    if let Ok(view) = favorites() {
        out.push_str(&format!(
            "favorites: total={} pending={} avatars={} worlds={}\n",
            view.total,
            view.pending,
            view.avatars.len(),
            view.worlds.len()
        ));
        for entry in view.avatars.iter().filter(|entry| entry.cached).take(2) {
            out.push_str(&format!("  cached avatar {} {}\n", entry.id, entry.name));
        }
    }

    let current = settings::load();
    out.push_str(&format!(
        "settings: refresh={}s show_offline={} animate={}\n",
        current.refresh_seconds, current.show_offline, current.animate_backdrop
    ));

    let runtime = match tokio::runtime::Runtime::new() {
        Ok(runtime) => runtime,
        Err(error) => {
            out.push_str(&format!("runtime error: {error}\n"));
            let _ = fs::write("shell-dump.txt", out);
            return;
        }
    };

    let api = Arc::new(Api::new());
    runtime.block_on(async {
        match api.restore().await {
            Some(user) => out.push_str(&format!(
                "auth: signed in as {} ({} / {})\n",
                user.display_name, user.status, user.trust
            )),
            None => out.push_str("auth: not signed in (no usable local session)\n"),
        }

        api.start_realtime();
        tokio::time::sleep(Duration::from_secs(20)).await;
        let (entries, online, sample) = api.presence_summary();
        out.push_str(&format!(
            "realtime: live={} presence_entries={} presence_online={} sample={:?} events={:?} error={}\n",
            api.live(),
            entries,
            online,
            sample,
            api.event_counts(),
            api.last_error()
        ));
        out.push_str(&format!("location: {}\n", api.location()));

        let quick_started = std::time::Instant::now();
        match api.friends_quick().await {
            Ok(quick) => out.push_str(&format!(
                "friends_quick: {} items in {} ms complete={}\n",
                quick.total,
                quick_started.elapsed().as_millis(),
                quick.complete
            )),
            Err(error) => out.push_str(&format!("friends_quick error: {error}\n")),
        }

        let roster_started = std::time::Instant::now();
        match api.friends().await {
            Ok(roster) => {
                out.push_str(&format!(
                    "roster: {} total, {} online, live={}, degraded={}, {} ms\n",
                    roster.total,
                    roster.online,
                    roster.live,
                    roster.degraded,
                    roster_started.elapsed().as_millis()
                ));
                for friend in roster.items.iter().filter(|friend| friend.online).take(8) {
                    out.push_str(&format!(
                        "  {} | {} | {} | {}\n",
                        friend.name, friend.status, friend.world, friend.instance
                    ));
                }
            }
            Err(error) => out.push_str(&format!("roster error: {error}\n")),
        }

        match api.instance().await {
            Ok(view) => out.push_str(&format!(
                "instance: in_instance={} world={} type={} players={} notice={}\n",
                view.in_instance,
                view.world_name,
                view.instance_type,
                view.players.len(),
                view.notice
            )),
            Err(error) => out.push_str(&format!("instance error: {error}\n")),
        }

        match api.notifications().await {
            Ok(notices) => {
                let unread = notices.iter().filter(|notice| !notice.seen).count();
                out.push_str(&format!(
                    "notifications: {} total, {} unread\n",
                    notices.len(),
                    unread
                ));
            }
            Err(error) => out.push_str(&format!("notifications error: {error}\n")),
        }
    });

    let _ = fs::write("shell-dump.txt", out);
}

/// Headless version of what the Favorites pane does: the same cache, the same
/// two-at-a-time lookups, written to a report because the release build has no
/// console.
fn resolve_favorites_report(passes: usize) {
    let mut out = String::new();
    let api = Arc::new(Api::new());
    let runtime = tokio::runtime::Runtime::new().expect("runtime");
    runtime.block_on(async {
        match api.restore().await {
            Some(user) => out.push_str(&format!("auth: {}\n", user.display_name)),
            None => {
                out.push_str("auth: not signed in\n");
                fs::write("shell-favorites.txt", &out).ok();
                return;
            }
        }

        let (avatar_ids, world_ids) = favorites::ids(&data_dir());
        let mut store = favorites::load();
        let start = favorites::view(&store, &avatar_ids, &world_ids);
        out.push_str(&format!(
            "before: total={} pending={}\n",
            start.total, start.pending
        ));

        for pass in 1..=passes {
            let (ask_avatars, ask_worlds) = favorites::pending(&store, &avatar_ids, &world_ids, 2);
            if ask_avatars.is_empty() && ask_worlds.is_empty() {
                break;
            }
            out.push_str(&format!(
                "pass {pass}: avatars={ask_avatars:?} worlds={ask_worlds:?}\n"
            ));

            let mut got = 0usize;
            let mut gone = 0usize;
            let (found, rejected) = api.avatar_details(&ask_avatars).await;
            for (id, name, thumbnail) in found {
                out.push_str(&format!(
                    "  avatar {id} -> {name} | {}\n",
                    if thumbnail.is_empty() {
                        "no thumbnail"
                    } else {
                        "thumbnail ok"
                    }
                ));
                favorites::apply(&mut store, "avatar", id, name, thumbnail);
                got += 1;
            }
            for id in rejected {
                out.push_str(&format!("  avatar {id} -> unavailable\n"));
                favorites::mark_missing(&mut store, "avatar", id);
                gone += 1;
            }

            let (found, rejected) = api.world_details(&ask_worlds).await;
            for (id, name, thumbnail) in found {
                out.push_str(&format!(
                    "  world {id} -> {name} | {}\n",
                    if thumbnail.is_empty() {
                        "no thumbnail"
                    } else {
                        "thumbnail ok"
                    }
                ));
                favorites::apply(&mut store, "world", id, name, thumbnail);
                got += 1;
            }
            for id in rejected {
                out.push_str(&format!("  world {id} -> unavailable\n"));
                favorites::mark_missing(&mut store, "world", id);
                gone += 1;
            }
            if got == 0 && gone == 0 {
                out.push_str("  no details returned (rate limited or offline)\n");
            }
            if let Err(error) = favorites::save(&store) {
                out.push_str(&format!("  save error: {error}\n"));
            }

            let view = favorites::view(&store, &avatar_ids, &world_ids);
            out.push_str(&format!(
                "  after pass: cached={} pending={}\n",
                view.total - view.pending,
                view.pending
            ));
            tokio::time::sleep(std::time::Duration::from_millis(2200)).await;
        }

        let end = favorites::view(&store, &avatar_ids, &world_ids);
        out.push_str(&format!(
            "after: total={} pending={}\n",
            end.total, end.pending
        ));
        for entry in end.avatars.iter().filter(|entry| entry.cached).take(5) {
            out.push_str(&format!(
                "  {} {} {}\n",
                entry.id,
                entry.name,
                if entry.thumbnail.is_empty() {
                    "(no image)"
                } else {
                    "(image)"
                }
            ));
        }
    });

    fs::write("shell-favorites.txt", out).ok();
}

/// Test-only headless update: check, download, install and exit, writing what
/// happened next to the shell's settings. Built with `--features update-test`.
#[cfg(feature = "update-test")]
fn start_headless_update(handle: tauri::AppHandle) {
    tauri::async_runtime::spawn(async move {
        use tauri_plugin_updater::UpdaterExt;

        let plugins =
            serde_json::to_value(&handle.config().plugins).unwrap_or(serde_json::Value::Null);
        let endpoints = plugins
            .get("updater")
            .and_then(|updater| updater.get("endpoints"))
            .cloned()
            .unwrap_or(serde_json::Value::Null);
        let mut report = format!("current {}\n", env!("CARGO_PKG_VERSION"));
        report.push_str(&format!(
            "endpoints: {}\n",
            serde_json::to_string(&endpoints).unwrap_or_default()
        ));
        match handle.updater() {
            Ok(updater) => match updater.check().await {
                Ok(Some(update)) => {
                    report.push_str(&format!("offered {}\n", update.version));
                    match update.download_and_install(|_, _| {}, || {}).await {
                        Ok(()) => report.push_str("downloaded and installed\n"),
                        Err(error) => report.push_str(&format!("install failed: {error}\n")),
                    }
                }
                Ok(None) => report.push_str("no update offered\n"),
                Err(error) => report.push_str(&format!("check failed: {error}\n")),
            },
            Err(error) => report.push_str(&format!("updater unavailable: {error}\n")),
        }

        let _ = std::fs::create_dir_all(session::shell_dir());
        let path = session::shell_dir().join("update-report.txt");
        // Append: after an install the app is relaunched with the same arguments,
        // so both runs must stay visible.
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|elapsed| elapsed.as_secs())
            .unwrap_or(0);
        let mut body = std::fs::read_to_string(&path).unwrap_or_default();
        body.push_str(&format!("=== run {stamp}\n{report}"));
        std::fs::write(&path, body).ok();
        handle.exit(0);
    });
}

fn main() {
    let arguments: Vec<String> = std::env::args().collect();
    if arguments.iter().any(|argument| argument == "--resolve-favorites") {
        let passes = arguments
            .iter()
            .position(|argument| argument == "--resolve-favorites")
            .and_then(|index| arguments.get(index + 1))
            .and_then(|value| value.parse::<usize>().ok())
            .unwrap_or(3);
        resolve_favorites_report(passes.clamp(1, 20));
        return;
    }
    if arguments.iter().any(|argument| argument == "--dump") {
        dump_report();
        return;
    }

    tauri::Builder::default()
        .plugin(tauri_plugin_updater::Builder::new().build())
        // Lets the update path be exercised without a mouse: check, download,
        // install and exit, writing what happened next to the shell's settings.
        .setup(|_app| {
            // Compiled only with the `update-test` feature: a shipping build has
            // no headless update switch at all.
            #[cfg(feature = "update-test")]
            if std::env::args().any(|argument| argument == "--install-update") {
                start_headless_update(_app.handle().clone());
            }
            Ok(())
        })
        .manage(Arc::new(Api::new()))
        .register_asynchronous_uri_scheme_protocol("kawaii", |context, request, responder| {
            let app = context.app_handle().clone();
            tauri::async_runtime::spawn(async move {
                let query = request.uri().query().unwrap_or_default().to_string();
                let mut image_url = None;
                let mut file_path = None;
                for (key, value) in url::form_urlencoded::parse(query.as_bytes()) {
                    match key.as_ref() {
                        "u" => image_url = Some(value.into_owned()),
                        "f" => file_path = Some(value.into_owned()),
                        _ => {}
                    }
                }

                let response = if let Some(url) = image_url {
                    let api = app.state::<Arc<Api>>().inner().clone();
                    match api.image(&url).await {
                        Ok((mime, bytes)) => tauri::http::Response::builder()
                            .header("Content-Type", mime)
                            .header("Cache-Control", "private, max-age=600")
                            .body(bytes)
                            .unwrap(),
                        Err(_) => tauri::http::Response::builder()
                            .status(404)
                            .body(Vec::new())
                            .unwrap(),
                    }
                } else if let Some(path) = file_path.and_then(|path| local_media_path(&path)) {
                    let mime = mime_for(&path);
                    match tokio::fs::read(&path).await {
                        Ok(bytes) => tauri::http::Response::builder()
                            .header("Content-Type", mime)
                            .header("Cache-Control", "private, max-age=600")
                            .body(bytes)
                            .unwrap(),
                        Err(_) => tauri::http::Response::builder()
                            .status(404)
                            .body(Vec::new())
                            .unwrap(),
                    }
                } else {
                    tauri::http::Response::builder()
                        .status(400)
                        .body(Vec::new())
                        .unwrap()
                };

                responder.respond(response);
            });
        })
        .invoke_handler(tauri::generate_handler![
            summary,
            feed,
            instances,
            media,
            auth_state,
            sign_in,
            verify_two_factor,
            sign_out,
            friends,
            friends_quick,
            friends_snapshot,
            friend_worlds,
            instance,
            notifications,
            settings_get,
            settings_save,
            check_update,
            install_update,
            verify_manifest,
            merge_activity,
            favorites,
            refresh_favorites,
            send_chatbox
        ])
        .run(tauri::generate_context!())
        .expect("failed to start the KawaiiVrc shell");
}
