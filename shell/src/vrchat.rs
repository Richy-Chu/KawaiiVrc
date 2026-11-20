use crate::session;
use futures::future::join_all;
use futures::sink::SinkExt;
use futures::stream::StreamExt;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::Message;

const BASE: &str = "https://api.vrchat.cloud/api/1/";
const PIPELINE: &str = "wss://pipeline.vrchat.cloud";
const USER_AGENT: &str = "KawaiiVrcShell/0.2.0 (unofficial Windows companion)";

/// The shell's own cache of world names, beside its keys rather than in the WPF
/// app's folder, so a relaunch does not redo the lookups. Older builds shared
/// one file for every account; it is adopted by the first account that asks.
const WORLD_CACHE_FILE: &str = "world-names.json";
const WORLD_CACHE_LIMIT: u64 = 4 * 1024 * 1024;
/// The last complete roster, so a launch paints friends before any request.
/// One file per account: a snapshot from one account must never be painted for
/// another, so the file name carries the account id.
const ROSTER_CACHE_PREFIX: &str = "roster-snapshot";
const ROSTER_CACHE_LIMIT: u64 = 16 * 1024 * 1024;
/// A saved roster older than this is not painted at all: after a week its names
/// are more likely to mislead than help, so the live walk starts from scratch.
const ROSTER_CACHE_MAX_AGE_SECS: u64 = 7 * 24 * 60 * 60;
/// World names are re-looked up a week after they were learned, so a renamed
/// world does not stay wrong forever.
const WORLD_NAME_TTL_SECS: u64 = 7 * 24 * 60 * 60;
/// The world cache keeps a little more than the roster needs, so a large friend
/// list does not lose worlds it uses, without letting the file grow forever.
const WORLD_CACHE_MIN: usize = 500;
const WORLD_CACHE_HEADROOM: usize = 250;
const WORLD_CACHE_MAX: usize = 20_000;

fn world_cache_cap(friends: usize) -> usize {
    (friends + WORLD_CACHE_HEADROOM).clamp(WORLD_CACHE_MIN, WORLD_CACHE_MAX)
}

/// A VRChat user id is already file-safe, but never trust a value from the
/// network with a path.
fn cache_key(user_id: &str) -> String {
    let cleaned: String = user_id
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric() || *ch == '-' || *ch == '_')
        .take(64)
        .collect();
    if cleaned.is_empty() {
        "unknown".to_string()
    } else {
        cleaned
    }
}

fn world_cache_path(account: &str) -> PathBuf {
    session::shell_dir().join(format!("world-names-{account}.json"))
}

/// The per-account world cache, taking over an older build's shared file once.
/// World names are public data rather than one account's, so adopting it is safe
/// and keeps the first launch after an upgrade warm.
fn account_world_cache_path(account: &str) -> PathBuf {
    let path = world_cache_path(account);
    if !path.exists() {
        let legacy = session::shell_dir().join(WORLD_CACHE_FILE);
        if legacy.exists() {
            let _ = std::fs::rename(&legacy, &path);
        }
    }
    path
}

fn roster_cache_path(account: &str) -> PathBuf {
    session::shell_dir().join(format!("{ROSTER_CACHE_PREFIX}-{account}.json"))
}

/// The saved roster is a starting picture, not a live one: it is always treated
/// as complete with revision 0, so the first publish of a launch supersedes it.
fn load_roster_cache(path: &Path) -> Option<Roster> {
    let readable = std::fs::metadata(path)
        .map(|meta| meta.len() <= ROSTER_CACHE_LIMIT)
        .unwrap_or(false);
    if !readable {
        return None;
    }
    let mut roster: Roster = std::fs::read_to_string(path)
        .ok()
        .and_then(|text| serde_json::from_str(&text).ok())?;
    if roster.items.is_empty() {
        return None;
    }
    // Snapshots written before the timestamp existed are dated by the file's
    // own age instead of being trusted as fresh.
    let saved_at = if roster.saved_at > 0 {
        roster.saved_at
    } else {
        std::fs::metadata(path)
            .and_then(|meta| meta.modified())
            .ok()
            .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|elapsed| elapsed.as_secs())
            .unwrap_or(0)
    };
    if saved_at > 0 && now_secs().saturating_sub(saved_at) > ROSTER_CACHE_MAX_AGE_SECS {
        // Too old to be an honest starting picture; do not paint it, and do not
        // keep parsing it on every launch.
        let _ = std::fs::remove_file(path);
        return None;
    }

    roster.revision = 0;
    roster.stale = true;
    roster.live = false;
    roster.complete = true;
    roster.degraded = false;
    roster.saved_at = saved_at;
    roster.total = roster.items.len();
    roster.online = roster.items.iter().filter(|friend| friend.online).count();
    Some(roster)
}

/// Removes the single-account snapshot older builds left behind; the
/// per-account files replaced it and nothing reads it any more.
fn retire_legacy_roster_cache(dir: &Path) {
    let legacy = dir.join(format!("{ROSTER_CACHE_PREFIX}.json"));
    if legacy.exists() {
        let _ = std::fs::remove_file(&legacy);
    }
}

fn save_roster_cache(path: &Path, roster: &Roster) {
    if roster.items.is_empty() {
        return;
    }
    // The file remembers how old it is, so a later launch can label or drop it.
    let mut saved = roster.clone();
    saved.saved_at = now_secs();
    if let Ok(json) = serde_json::to_string(&saved) {
        write_json_atomically(path, &json);
    }
}

fn now_secs() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_secs())
        .unwrap_or(0)
}

/// A VRChat error body carries the useful text; 401 means the session is gone.
fn api_message(status: reqwest::StatusCode, text: &str) -> String {
    if status == reqwest::StatusCode::UNAUTHORIZED {
        return "Your session expired. Sign in again.".to_string();
    }
    let detail = serde_json::from_str::<Value>(text)
        .ok()
        .and_then(|body| {
            body.get("error")
                .and_then(|error| error.get("message"))
                .and_then(Value::as_str)
                .map(|message| message.trim().trim_matches('"').to_string())
        })
        .filter(|message| !message.is_empty());
    detail.unwrap_or_else(|| format!("VRChat returned {} for that action.", status.as_u16()))
}

/// Only ids that look like VRChat ids go into a request path.
fn path_id(value: &str) -> Result<String, String> {
    let value = value.trim();
    let valid = !value.is_empty()
        && value.len() <= 128
        && value
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '-' || ch == '_');
    if valid {
        Ok(value.to_string())
    } else {
        Err("That id does not look like a VRChat id.".to_string())
    }
}

/// The notification types with something to accept.
fn actionable_kind(kind: &str) -> bool {
    matches!(kind, "friendRequest" | "invite" | "requestInvite")
}

/// The instance an invite should point at, or why there is none.
fn invite_instance_from(location: &str) -> Result<String, String> {
    let location = location.trim();
    let allowed = location
        .chars()
        .all(|ch| ch.is_ascii_alphanumeric() || "~:()-_".contains(ch));
    if location.starts_with("wrld_") && location.contains(':') && allowed {
        return Ok(location.to_string());
    }
    Err("Join a world first: there is no instance to invite anyone to.".to_string())
}

#[derive(Serialize, Clone)]
pub struct NoticeOverview {
    /// One page of notifications, starting at `offset`.
    pub notices: Vec<Notice>,
    /// Every waiting actionable notification across the whole list.
    pub waiting: usize,
    /// Where this page starts, so the window can ask for the next one.
    pub offset: usize,
    /// True when the page was full and another one may follow.
    pub has_more: bool,
}

/// The counts behind the badge. A full walk of the list is only worth doing
/// occasionally, so the first page's ids are tracked between walks: new ones are
/// added, and ones that disappear from the page are checked individually before
/// they are subtracted, because a new arrival can push a waiting request off the
/// page without answering it.
#[derive(Default, Clone)]
struct NoticeCount {
    /// Every actionable id still believed to be waiting; the total is its size.
    ids: HashSet<String>,
    /// The actionable ids that were on the first page last time.
    page_ids: HashSet<String>,
    /// Ids that left the first page and are waiting to be checked.
    checks: VecDeque<String>,
    at: u64,
}

const NOTICE_PAGE: usize = 100;
const NOTICE_MAX_OFFSET: usize = 10_000;
/// How often the cheap count is corrected by walking the whole list.
const NOTICE_RECOUNT_SECS: u64 = 30 * 60;
/// Removals checked per refresh: one request each, spent on the newest
/// disappearances first, so the count converges without a walk.
const NOTICE_CHECK_BUDGET: usize = 5;

fn actionable_ids(rows: &[Value]) -> HashSet<String> {
    rows.iter()
        .filter(|row| actionable_kind(&text(row, "type")))
        .filter_map(|row| row.get("id").and_then(Value::as_str).map(str::to_string))
        .collect()
}

fn page_has_more(rows: usize) -> bool {
    rows >= NOTICE_PAGE
}

/// Folds one first page into the count: new ids are added, and ids that left
/// the page are queued for a check rather than subtracted on the assumption
/// they were answered.
fn merge_notice_page(cache: &mut NoticeCount, current: HashSet<String>) {
    for id in cache.page_ids.difference(&current) {
        if cache.ids.contains(id) && !cache.checks.iter().any(|queued| queued == id) {
            cache.checks.push_back(id.clone());
        }
    }
    cache.ids.extend(current.iter().cloned());
    cache.page_ids = current;
}

/// Applies what VRChat said about one id: gone means answered elsewhere, so it
/// stops counting; still there means it was only pushed down the list.
fn settle_notice_check(cache: &mut NoticeCount, id: &str, alive: bool) {
    cache.checks.retain(|queued| queued != id);
    if !alive {
        cache.ids.remove(id);
    }
}

fn notice_from(row: &Value) -> Notice {
    let sender = {
        let display = text(row, "senderDisplayName");
        if display.is_empty() {
            text(row, "senderUsername")
        } else {
            display
        }
    };
    let created = {
        let modern = text(row, "createdAt");
        if modern.is_empty() {
            text(row, "created_at")
        } else {
            modern
        }
    };
    Notice {
        id: text(row, "id"),
        kind: text(row, "type"),
        message: text(row, "message"),
        sender,
        sender_id: text(row, "senderUserId"),
        location: notice_location(row),
        created_at: created,
        seen: row.get("seen").and_then(Value::as_bool).unwrap_or(false),
    }
}

/// The instance an invite notification points at, if it names one.
fn notice_location(row: &Value) -> String {
    let details = row.get("details");
    if let Some(object) = details.and_then(Value::as_object) {
        let world = object.get("worldId").and_then(Value::as_str).unwrap_or_default();
        let instance = object.get("instanceId").and_then(Value::as_str).unwrap_or_default();
        if !world.is_empty() && !instance.is_empty() {
            return format!("{world}:{instance}");
        }
        let location = object
            .get("location")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if !location.is_empty() {
            return location.to_string();
        }
    }
    // Details are sometimes the JSON blob as a string.
    if let Some(text) = details.and_then(Value::as_str) {
        let trimmed = text.trim();
        if trimmed.starts_with("wrld_") {
            return trimmed.to_string();
        }
        if let Ok(value) = serde_json::from_str::<Value>(trimmed) {
            return notice_location(&serde_json::json!({ "details": value }));
        }
    }
    String::new()
}

/// Friend favorites from `GET /favorites?type=friend`, keyed by user id with the
/// favorite row id as the value (that row id is what DELETE needs).
fn friend_favorites_from(text: &str) -> HashMap<String, String> {
    serde_json::from_str::<Vec<Value>>(text)
        .unwrap_or_default()
        .iter()
        .filter(|row| row.get("type").and_then(Value::as_str) == Some("friend"))
        .filter_map(|row| {
            let user = row.get("favoriteId").and_then(Value::as_str)?;
            let row_id = row.get("id").and_then(Value::as_str)?;
            Some((user.to_string(), row_id.to_string()))
        })
        .collect()
}

#[derive(Default)]
struct WorldCache {
    names: HashMap<String, String>,
    /// When each name was learned, so old entries can expire and the file can
    /// be pruned oldest-first.
    stamps: HashMap<String, u64>,
}

fn load_world_cache(path: &Path) -> WorldCache {
    let mut cache = WorldCache::default();
    let readable = std::fs::metadata(path)
        .map(|meta| meta.len() <= WORLD_CACHE_LIMIT)
        .unwrap_or(false);
    if !readable {
        return cache;
    }
    let Ok(text) = std::fs::read_to_string(path) else {
        return cache;
    };
    let Ok(raw) = serde_json::from_str::<HashMap<String, Value>>(&text) else {
        return cache;
    };

    let now = now_secs();
    let mut entries: Vec<(String, String, u64)> = Vec::new();
    for (id, value) in raw {
        if !id.starts_with("wrld_") {
            continue;
        }
        let (name, at) = match value {
            // Older builds wrote a bare name. Date it now so the file still
            // loads and gets a full TTL from this build onward.
            Value::String(name) => (name, now),
            Value::Object(fields) => (
                fields
                    .get("name")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_string(),
                fields.get("at").and_then(Value::as_u64).unwrap_or(now),
            ),
            _ => continue,
        };
        if name.is_empty() || now.saturating_sub(at) > WORLD_NAME_TTL_SECS {
            continue;
        }
        entries.push((id, name, at));
    }

    // A file written by an unbounded build may be oversized; keep the newest.
    entries.sort_by(|left, right| right.2.cmp(&left.2));
    entries.truncate(WORLD_CACHE_MAX);
    for (id, name, at) in entries {
        cache.names.insert(id.clone(), name);
        cache.stamps.insert(id, at);
    }
    cache
}

fn write_json_atomically(path: &Path, json: &str) {
    let temp = path.with_extension("tmp");
    if std::fs::write(&temp, json).is_ok() {
        let _ = std::fs::rename(&temp, path);
    }
}

fn save_world_cache(
    path: &Path,
    names: &HashMap<String, String>,
    stamps: &HashMap<String, u64>,
    cap: usize,
) {
    let now = now_secs();
    let mut entries: Vec<(&String, &String, u64)> = names
        .iter()
        // Failed lookups stay in memory only; they deserve a retry next launch.
        .filter(|(_, name)| !name.is_empty())
        .map(|(id, name)| (id, name, stamps.get(id).copied().unwrap_or(now)))
        .filter(|(_, _, at)| now.saturating_sub(*at) <= WORLD_NAME_TTL_SECS)
        .collect();
    entries.sort_by(|left, right| right.2.cmp(&left.2));
    entries.truncate(cap);
    if entries.is_empty() {
        return;
    }

    let file: BTreeMap<&String, Value> = entries
        .iter()
        .map(|(id, name, at)| (*id, serde_json::json!({ "name": name, "at": at })))
        .collect();
    if let Ok(json) = serde_json::to_string(&file) {
        write_json_atomically(path, &json);
    }
}

#[derive(Serialize, Clone)]
pub struct UserInfo {
    pub id: String,
    pub display_name: String,
    pub avatar: String,
    pub status: String,
    pub trust: String,
    pub location: String,
}

#[derive(Serialize, Clone)]
pub struct Player {
    pub id: String,
    pub name: String,
    pub status: String,
    pub avatar: String,
    pub is_friend: bool,
    pub trust: String,
}

#[derive(Serialize, Clone)]
pub struct PresenceUpdate {
    pub friend: Friend,
    pub online: usize,
    pub total: usize,
}

#[derive(Serialize, Clone)]
pub struct InstanceView {
    pub in_instance: bool,
    pub location: String,
    pub world_id: String,
    pub world_name: String,
    pub world_thumbnail: String,
    pub instance_type: String,
    pub capacity: i64,
    pub players: Vec<Player>,
    pub notice: String,
}

#[derive(Serialize, Clone)]
pub struct Notice {
    pub id: String,
    pub kind: String,
    pub message: String,
    pub sender: String,
    pub sender_id: String,
    /// The instance an invite points at, when it names one; used to invite the
    /// account itself in, since the shell cannot join a world.
    pub location: String,
    pub created_at: String,
    pub seen: bool,
}

#[derive(Serialize, Deserialize, Clone, Default)]
#[serde(default)]
pub struct Friend {
    pub id: String,
    pub name: String,
    pub status: String,
    pub status_description: String,
    pub location: String,
    pub world: String,
    pub instance: String,
    pub avatar: String,
    pub online: bool,
    pub trust: String,
}

#[derive(Serialize, Deserialize, Clone, Default)]
// Missing keys fall back to defaults so a cache written by an older build still
// loads instead of being thrown away.
#[serde(default)]
pub struct Roster {
    pub items: Vec<Friend>,
    pub total: usize,
    pub online: usize,
    /// Bumped every time a roster is published, so pollers can skip unchanged
    /// snapshots instead of re-rendering the list.
    pub revision: u64,
    /// True while the rows come from the saved roster, before this launch's
    /// first live publish. It is serialized so the window can label the list.
    pub stale: bool,
    pub live: bool,
    /// In-world friends whose world name is not cached yet; the UI keeps
    /// reading the cache while the background lookup fills it in.
    pub worlds_pending: usize,
    /// False while only the first page has been loaded.
    pub complete: bool,
    /// True when some pages were dropped after retries, so the list may be short.
    pub degraded: bool,
    /// When the saved snapshot was written, as a Unix timestamp; 0 for a live
    /// roster. The window turns it into "saved 3 h ago" while it is stale.
    pub saved_at: u64,
    #[serde(skip)]
    pub missing_worlds: Vec<String>,
}

#[derive(Serialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum LoginStart {
    SignedIn { user: UserInfo },
    TwoFactor { methods: Vec<String> },
}

#[derive(Clone, Default)]
struct Presence {
    location: String,
    status: String,
    status_description: String,
    avatar: String,
    online: bool,
}

pub struct Api {
    http: reqwest::Client,
    /// Online-bucket pages the quick load already fetched, so the full walk can
    /// start after them instead of asking for them a second time.
    seed: Mutex<Vec<(usize, Vec<Value>)>>,
    /// Every friend-page HTTP request, keyed `bucket:offset`, so the paging
    /// behaviour can be checked from outside.
    page_requests: Mutex<HashMap<String, u32>>,
    revision: AtomicU64,
    app: Mutex<Option<tauri::AppHandle>>,
    /// The window keeps its caches on disk; tests and one-off reports do not.
    persist: bool,
    auth: Mutex<Option<String>>,
    user: Mutex<Option<UserInfo>>,
    /// The signed-in account whose cache files are in use.
    account: Mutex<Option<String>>,
    worlds: Mutex<HashMap<String, String>>,
    /// When each cached world name was learned; drives TTL and pruning.
    world_seen: Mutex<HashMap<String, u64>>,
    presence: Mutex<HashMap<String, Presence>>,
    self_location: Mutex<String>,
    activity: crate::activity::ActivityLog,
    events: Mutex<HashMap<String, u32>>,
    realtime_error: Mutex<String>,
    live: AtomicBool,
    realtime_started: AtomicBool,
    resolving: AtomicBool,
    roster: Mutex<Option<Roster>>,
    roster_loading: AtomicBool,
    notice_count: Mutex<Option<NoticeCount>>,
    /// Pages fetched for notifications, and how many of those were part of a
    /// full count walk. Kept so the cost of the badge can be checked.
    notice_pages: AtomicU32,
    notice_walks: AtomicU32,
    notice_checks: AtomicU32,
}

impl Api {
    pub fn new() -> Self {
        Self::build(false)
    }

    /// The window's API: it remembers the last roster and world names on disk.
    pub fn with_disk_cache() -> Self {
        Self::build(true)
    }

    fn build(persist: bool) -> Self {
        let http = reqwest::Client::builder()
            .user_agent(USER_AGENT)
            .timeout(Duration::from_secs(25))
            .build()
            .expect("http client");
        Self {
            http,
            persist,
            seed: Mutex::new(Vec::new()),
            page_requests: Mutex::new(HashMap::new()),
            revision: AtomicU64::new(0),
            app: Mutex::new(None),
            auth: Mutex::new(None),
            user: Mutex::new(None),
            account: Mutex::new(None),
            worlds: Mutex::new(HashMap::new()),
            world_seen: Mutex::new(HashMap::new()),
            presence: Mutex::new(HashMap::new()),
            self_location: Mutex::new(String::new()),
            activity: crate::activity::ActivityLog::new(),
            events: Mutex::new(HashMap::new()),
            realtime_error: Mutex::new(String::new()),
            live: AtomicBool::new(false),
            resolving: AtomicBool::new(false),
            realtime_started: AtomicBool::new(false),
            roster: Mutex::new(None),
            roster_loading: AtomicBool::new(false),
            notice_count: Mutex::new(None),
            notice_pages: AtomicU32::new(0),
            notice_walks: AtomicU32::new(0),
            notice_checks: AtomicU32::new(0),
        }
    }

    fn cookie(&self) -> Option<String> {
        self.auth.lock().ok().and_then(|guard| guard.clone())
    }

    fn set_cookie(&self, value: Option<String>) {
        if let Ok(mut guard) = self.auth.lock() {
            *guard = value;
        }
    }

    pub fn current_user(&self) -> Option<UserInfo> {
        self.user.lock().ok().and_then(|guard| guard.clone())
    }

    fn set_user(&self, value: Option<UserInfo>) {
        if let Ok(mut guard) = self.user.lock() {
            *guard = value;
        }
    }

    fn current_account(&self) -> Option<String> {
        self.account.lock().ok().and_then(|guard| guard.clone())
    }

    /// Points the in-memory caches at one account's files. Called every time a
    /// session is established, so a different account can never be shown the
    /// roster or world names the previous one left behind.
    fn adopt_account(&self, user_id: &str) {
        let key = cache_key(user_id);
        {
            let mut current = self.account.lock().unwrap_or_else(|error| error.into_inner());
            if current.as_deref() == Some(key.as_str()) {
                return;
            }
            *current = Some(key.clone());
        }

        if self.persist {
            retire_legacy_roster_cache(&session::shell_dir());
        }

        // Drop everything that belonged to the previous account before loading
        // this one's files.
        if let Ok(mut roster) = self.roster.lock() {
            *roster = None;
        }
        self.revision.store(0, Ordering::SeqCst);
        if let Ok(mut presence) = self.presence.lock() {
            presence.clear();
        }
        self.invalidate_notice_count();

        // Tests and one-off reports keep no cache; only the window reads one.
        if !self.persist {
            return;
        }

        if let Ok(mut roster) = self.roster.lock() {
            *roster = load_roster_cache(&roster_cache_path(&key));
        }

        let world_cache = load_world_cache(&account_world_cache_path(&key));
        if let Ok(mut worlds) = self.worlds.lock() {
            *worlds = world_cache.names;
        }
        if let Ok(mut seen) = self.world_seen.lock() {
            *seen = world_cache.stamps;
        }
    }

    async fn request(
        &self,
        method: reqwest::Method,
        path: &str,
        cookie: Option<&str>,
        body: Option<Value>,
    ) -> Result<(reqwest::StatusCode, reqwest::header::HeaderMap, String), String> {
        let mut builder = self.http.request(method, format!("{BASE}{path}"));
        if let Some(cookie) = cookie {
            builder = builder.header(reqwest::header::COOKIE, format!("auth={cookie}"));
        }
        if let Some(body) = body {
            builder = builder.json(&body);
        }
        let response = builder.send().await.map_err(network_error)?;
        let status = response.status();
        let headers = response.headers().clone();
        let text = response.text().await.unwrap_or_default();
        Ok((status, headers, text))
    }

    // ----------------------------------------------------------------- login

    pub async fn login(&self, username: &str, password: &str) -> Result<LoginStart, String> {
        let response = self
            .http
            .get(format!("{BASE}auth/user"))
            .basic_auth(username, Some(password))
            .send()
            .await
            .map_err(network_error)?;
        let status = response.status();
        let cookie = auth_cookie(response.headers());
        let text = response.text().await.unwrap_or_default();
        let body: Value = serde_json::from_str(&text).unwrap_or(Value::Null);

        if let Some(methods) = two_factor_methods(&body) {
            if cookie.is_some() {
                self.set_cookie(cookie);
            }
            return Ok(LoginStart::TwoFactor { methods });
        }

        if !status.is_success() {
            return Err(login_error(status, &body));
        }

        let cookie = cookie.ok_or_else(|| "VRChat did not return a session cookie.".to_string())?;
        self.set_cookie(Some(cookie.clone()));
        let user = self.fetch_user().await?;
        session::save_shell_cookie(&cookie);
        Ok(LoginStart::SignedIn { user })
    }

    pub async fn verify_two_factor(&self, code: &str, method: &str) -> Result<UserInfo, String> {
        let cookie = self
            .cookie()
            .ok_or_else(|| "The sign-in attempt expired. Start again.".to_string())?;
        let path = match method {
            "emailotp" => "auth/twofactorauth/emailotp/verify",
            "otp" => "auth/twofactorauth/otp/verify",
            _ => "auth/twofactorauth/totp/verify",
        };

        let (status, headers, text) = self
            .request(
                reqwest::Method::POST,
                path,
                Some(&cookie),
                Some(serde_json::json!({ "code": code })),
            )
            .await?;

        if let Some(updated) = auth_cookie(&headers) {
            self.set_cookie(Some(updated));
        }

        if !status.is_success() {
            return Err(match status.as_u16() {
                401 => "That code was rejected. Check it and try again.".to_string(),
                _ => format!("VRChat returned {} for the code.", status.as_u16()),
            });
        }

        if let Ok(body) = serde_json::from_str::<Value>(&text) {
            if body.get("verified").and_then(Value::as_bool) == Some(false) {
                return Err("That code was rejected. Check it and try again.".to_string());
            }
        }

        let user = self.fetch_user().await?;
        if let Some(cookie) = self.cookie() {
            session::save_shell_cookie(&cookie);
        }
        Ok(user)
    }

    async fn fetch_user(&self) -> Result<UserInfo, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, text) = self
            .request(reqwest::Method::GET, "auth/user", Some(&cookie), None)
            .await?;
        if status == reqwest::StatusCode::UNAUTHORIZED {
            return Err("VRChat rejected the saved session. Sign in again.".to_string());
        }
        if !status.is_success() {
            return Err(format!("VRChat returned {} for the account.", status.as_u16()));
        }
        let body: Value = serde_json::from_str(&text)
            .map_err(|_| "VRChat returned an unexpected account payload.".to_string())?;
        if body.get("requiresTwoFactorAuth").is_some() {
            return Err("VRChat still wants a two-factor code.".to_string());
        }
        let user = user_info(&body);
        if !user.location.is_empty() {
            if let Ok(mut location) = self.self_location.lock() {
                *location = user.location.clone();
            }
        }
        self.set_user(Some(user.clone()));
        self.adopt_account(&user.id);
        Ok(user)
    }

    /// Reuses whichever session already exists on this PC: the shell's own,
    /// then the WPF app's single session or active multi-account cookie.
    pub async fn restore(&self) -> Option<UserInfo> {
        if let Some(user) = self.current_user() {
            return Some(user);
        }

        let mut candidates: Vec<String> = Vec::new();
        if let Some(cookie) = session::load_shell_cookie() {
            candidates.push(cookie);
        }
        if let Some(cookie) = session::companion_cookie() {
            candidates.push(cookie);
        }
        if let Some(cookie) = session::account_cookie() {
            candidates.push(cookie);
        }

        for cookie in candidates {
            self.set_cookie(Some(cookie.clone()));
            if let Ok(user) = self.fetch_user().await {
                session::save_shell_cookie(&cookie);
                return Some(user);
            }
        }

        self.set_cookie(None);
        None
    }

    pub async fn sign_out(&self) {
        if let Some(cookie) = self.cookie() {
            let _ = self
                .request(reqwest::Method::PUT, "auth/logout", Some(&cookie), None)
                .await;
        }
        self.set_cookie(None);
        self.set_user(None);
        if let Ok(mut account) = self.account.lock() {
            *account = None;
        }
        if let Ok(mut roster) = self.roster.lock() {
            *roster = None;
        }
        self.revision.store(0, Ordering::SeqCst);
        self.invalidate_notice_count();
        if let Ok(mut presence) = self.presence.lock() {
            presence.clear();
        }
        self.live.store(false, Ordering::SeqCst);
        session::clear_shell_cookie();
    }

    // ---------------------------------------------------------------- roster

    pub async fn friends(&self) -> Result<Roster, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;

        // VRChat's `offline` filter is exclusive: `offline=false` is the online
        // bucket and carries real locations, `offline=true` reports everyone as
        // offline. Both are needed, and online is fetched first so the roster is
        // useful before the offline pages finish. Pages go six at a time because
        // a large account has ~23 offline pages.
        let mut users: Vec<Value> = Vec::new();
        let mut seen: HashSet<String> = HashSet::new();
        let mut degraded = false;
        // The quick load's pages are the start of this same walk.
        let mut seed = Some(
            self.seed
                .lock()
                .map(|mut guard| std::mem::take(&mut *guard))
                .unwrap_or_default(),
        );

        let mut finished = true;
        for bucket in ["false", "true"] {
            let mut offset = 0usize;
            let mut done = false;
            let mut started = false;

            if bucket == "false" {
                let seeded = seed.take().unwrap_or_default();
                let (next, seeded_done, reused) = take_seed(seeded, &mut users, &mut seen);
                offset = next;
                done = seeded_done;
                started = reused > 0;
                if started {
                    // Paint what the walk starts from before it fetches more.
                    self.publish(&users, false, degraded);
                }
            }

            if !done && !started {
                let first = self
                    .fetch_page(&cookie, bucket, 0)
                    .await
                    .map_err(PageFailure::to_message)?;
                done = first.len() < 100;
                for user in first {
                    push_unique(&mut users, &mut seen, user);
                }
                offset = 100;
                self.publish(&users, false, degraded);
            }

            while !done && users.len() < FRIEND_CAP && offset < 3_000 {
                let offsets: Vec<usize> = (0..6).map(|index| offset + index * 100).collect();
                let results = join_all(
                    offsets
                        .iter()
                        .map(|offset| self.fetch_page(&cookie, bucket, *offset)),
                )
                .await;
                let before = users.len();
                let (round_done, round_degraded) =
                    fold_page_round(&mut users, results).map_err(PageFailure::to_message)?;
                degraded |= round_degraded;
                // Folded pages skip the id check, so run them through it here.
                let appended: Vec<Value> = users.split_off(before);
                for user in appended {
                    push_unique(&mut users, &mut seen, user);
                }
                done = round_done;
                offset += 600;
                // Each round is a snapshot, so pages appear as they land.
                self.publish(&users, false, degraded);
            }

            finished = done;
            if users.len() >= FRIEND_CAP {
                break;
            }
        }

        let (users, degraded) = bucket_result(users, finished, degraded);
        Ok(self.publish(&users, true, degraded))
    }

    /// Stores the roster the UI polls, so a partly walked list is already on
    /// screen while the remaining pages load.
    fn publish(&self, users: &[Value], complete: bool, degraded: bool) -> Roster {
        let mut roster = self.build_roster(users, complete, degraded);

        if !complete {
            // Keep the rows already on screen (usually the roster loaded from
            // disk) so a launch grows the list instead of dropping back to the
            // first page and filling in again.
            let previous = self.roster.lock().ok().and_then(|guard| guard.clone());
            if let Some(previous) = previous.filter(|old| old.items.len() > roster.items.len()) {
                let worlds = self.world_names();
                let mut known: HashSet<String> =
                    roster.items.iter().map(|friend| friend.id.clone()).collect();
                for mut friend in previous.items {
                    if known.insert(friend.id.clone()) {
                        locate(&mut friend, &worlds);
                        roster.items.push(friend);
                    }
                }
                refresh_counts(&mut roster, &worlds);
            }
        }

        roster.revision = self.revision.fetch_add(1, Ordering::SeqCst) + 1;
        if complete && self.persist {
            if let Some(account) = self.current_account() {
                save_roster_cache(&roster_cache_path(&account), &roster);
            }
        }
        if let Ok(mut guard) = self.roster.lock() {
            *guard = Some(roster.clone());
        }
        roster
    }

    /// The first online page, plus a second one when the first page was full,
    /// so the UI paints in well under a second and accounts with more than 100
    /// online friends are not truncated.
    pub async fn friends_quick(&self) -> Result<Roster, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let first = self
            .fetch_page(&cookie, "false", 0)
            .await
            .map_err(PageFailure::to_message)?;
        let mut seed: Vec<(usize, Vec<Value>)> = vec![(0, first)];
        let mut users: Vec<Value> = seed[0].1.clone();
        let mut degraded = false;
        if needs_second_page(users.len()) {
            let second = self.fetch_page(&cookie, "false", 100).await;
            match fold_second_page(&mut users, second).map_err(PageFailure::to_message)? {
                Some(page) => seed.push((100, page)),
                None => degraded = true,
            }
        }
        // Hand these pages to the full walk so it continues from here.
        if let Ok(mut guard) = self.seed.lock() {
            *guard = seed;
        }

        Ok(self.publish(&users, false, degraded))
    }

    pub fn roster_snapshot(&self) -> Option<Roster> {
        let worlds = self.world_names();
        let mut guard = self.roster.lock().ok()?;
        let roster = guard.as_mut()?;

        // Names keep landing after the roster was built, so relabel the stored
        // rows before handing them out: a poll must never put a raw id back on a
        // row whose world is now cached.
        let mut relabelled = false;
        for friend in &mut roster.items {
            let before = (
                friend.world.clone(),
                friend.instance.clone(),
                friend.online,
            );
            locate(friend, &worlds);
            if (friend.world.clone(), friend.instance.clone(), friend.online) != before {
                relabelled = true;
            }
        }
        refresh_counts(roster, &worlds);

        // A relabelled list is a changed list, so it needs a new revision or a
        // poller keyed on the revision would skip a snapshot that did change.
        if relabelled {
            roster.revision = self.revision.fetch_add(1, Ordering::SeqCst) + 1;
        }
        Some(roster.clone())
    }

    /// Loads the full roster without blocking the caller.
    pub fn start_roster_refresh(self: &Arc<Self>) {
        if self.roster_loading.swap(true, Ordering::SeqCst) {
            return;
        }
        let api = Arc::clone(self);
        tokio::spawn(async move {
            if let Ok(mut roster) = api.friends().await {
                // The full roster can mention worlds the quick page never did.
                let missing = std::mem::take(&mut roster.missing_worlds);
                api.resolve_worlds_in_background(missing);
            }
            api.roster_loading.store(false, Ordering::SeqCst);
        });
    }

    fn build_roster(&self, users: &[Value], complete: bool, degraded: bool) -> Roster {
        let mut items: Vec<Friend> = users
            .iter()
            .map(|user| map_friend(user, &HashMap::new()))
            .collect();

        // The friends endpoint reports every friend as offline; live presence
        // only arrives on the pipeline, so overlay whatever it has told us.
        let presence = self
            .presence
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default();
        for friend in &mut items {
            let Some(entry) = presence.get(&friend.id) else {
                continue;
            };
            if !entry.location.is_empty() {
                friend.location = entry.location.clone();
            }
            if !entry.status.is_empty() {
                friend.status = entry.status.clone();
            }
            if !entry.status_description.is_empty() {
                friend.status_description = entry.status_description.clone();
            }
            if !entry.avatar.is_empty() {
                friend.avatar = entry.avatar.clone();
            }
        }

        // Names come from the session cache only; anything unknown is resolved
        // in the background so the roster can be painted straight away.
        let worlds = self.world_names();
        for friend in &mut items {
            locate(friend, &worlds);
        }

        let missing_worlds = missing_worlds(&items, &worlds);

        Roster {
            revision: 0,
            stale: false,
            total: items.len(),
            online: items.iter().filter(|friend| friend.online).count(),
            live: self.live.load(Ordering::SeqCst),
            worlds_pending: missing_worlds.len(),
            missing_worlds,
            complete,
            degraded,
            saved_at: 0,
            items,
        }
    }

    /// One page, retried with backoff while VRChat throttles or errs.
    async fn fetch_page(
        &self,
        cookie: &str,
        bucket: &str,
        offset: usize,
    ) -> Result<Vec<Value>, PageFailure> {
        let mut attempt: u32 = 0;
        loop {
            match self.fetch_page_once(cookie, bucket, offset).await {
                PageOutcome::Ready(page) => return Ok(page),
                PageOutcome::Failed(failure) => return Err(failure),
                PageOutcome::Throttled(retry_after) => {
                    attempt += 1;
                    if attempt >= PAGE_ATTEMPTS {
                        return Err(PageFailure::Other(format!(
                            "VRChat kept throttling friends page {offset}."
                        )));
                    }
                    tokio::time::sleep(backoff_delay(attempt, retry_after)).await;
                }
            }
        }
    }

    async fn fetch_page_once(&self, cookie: &str, bucket: &str, offset: usize) -> PageOutcome {
        if let Ok(mut counts) = self.page_requests.lock() {
            *counts.entry(format!("{bucket}:{offset}")).or_insert(0) += 1;
        }
        let path = format!("auth/user/friends?offline={bucket}&n=100&offset={offset}");
        let (status, headers, text) = match self
            .request(reqwest::Method::GET, &path, Some(cookie), None)
            .await
        {
            Ok(response) => response,
            // A dropped connection is worth another try, like a 503.
            Err(_) => return PageOutcome::Throttled(None),
        };

        if let Some(failure) = page_failure(status.as_u16()) {
            if matches!(failure, PageFailure::Expired) {
                self.set_cookie(None);
                self.set_user(None);
            }
            return PageOutcome::Failed(failure);
        }
        if !status.is_success() {
            return PageOutcome::Throttled(retry_after_of(&headers));
        }

        match serde_json::from_str::<Vec<Value>>(&text) {
            Ok(page) => PageOutcome::Ready(page),
            Err(_) => PageOutcome::Failed(PageFailure::Other(
                "VRChat returned an unexpected friend list.".to_string(),
            )),
        }
    }

    /// World ids the roster's rows cannot name yet, in first-seen order. Only
    /// the roster knows about worlds that the first page never showed, so this
    /// is what keeps their rows from settling on raw ids.
    pub fn missing_world_ids(&self, items: &[Friend]) -> Vec<String> {
        let cached = self.world_names();
        let mut ids: Vec<String> = Vec::new();
        for friend in items {
            let Some(id) = world_id_of(&friend.location) else {
                continue;
            };
            if !cached.contains_key(&id) && !ids.contains(&id) {
                ids.push(id);
            }
        }
        ids
    }

    /// Friend-page requests made so far, keyed `bucket:offset`.
    pub fn page_requests(&self) -> HashMap<String, u32> {
        self.page_requests
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default()
    }

    /// The window handle used to push presence changes to the UI.
    pub fn set_app(&self, handle: tauri::AppHandle) {
        if let Ok(mut guard) = self.app.lock() {
            *guard = Some(handle);
        }
    }

    pub fn world_names(&self) -> HashMap<String, String> {
        self.worlds
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default()
    }

    /// Resolves world names after the roster is already on screen. One sweep at
    /// a time, and a sweep only takes 40 ids, so it keeps going until every id
    /// the caller passed is cached (or a sweep stops making progress).
    pub fn resolve_worlds_in_background(self: &Arc<Self>, ids: Vec<String>) {
        if ids.is_empty() || self.resolving.swap(true, Ordering::SeqCst) {
            return;
        }
        let api = Arc::clone(self);
        let task = async move {
            if let Some(cookie) = api.cookie() {
                let sweep = Arc::clone(&api);
                sweep_worlds(ids, |pending| {
                    let sweep = Arc::clone(&sweep);
                    let cookie = cookie.clone();
                    async move {
                        let _ = sweep.resolve_worlds(pending.clone(), &cookie).await;
                        sweep.uncached_worlds(&pending)
                    }
                })
                .await;
            }
            api.resolving.store(false, Ordering::SeqCst);
        };

        // Callers are usually async commands, but a sync command runs on the
        // main thread where tokio has no reactor; Tauri's runtime is there for
        // that case (and for the headless dump, which has neither).
        match tokio::runtime::Handle::try_current() {
            Ok(handle) => {
                handle.spawn(task);
            }
            Err(_) => {
                tauri::async_runtime::spawn(task);
            }
        }
    }

    fn uncached_worlds(&self, ids: &[String]) -> Vec<String> {
        let cached = self.world_names();
        ids.iter()
            .filter(|id| !cached.contains_key(*id))
            .cloned()
            .collect()
    }

    /// World names are shared across rows and change rarely, so each id is
    /// fetched once per session and a failed lookup is remembered as empty.
    async fn resolve_worlds(&self, ids: Vec<String>, cookie: &str) -> HashMap<String, String> {
        let cached = self
            .worlds
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default();
        let missing: Vec<String> = ids
            .into_iter()
            .filter(|id| !cached.contains_key(id))
            .take(WORLD_SWEEP_BATCH)
            .collect();
        if missing.is_empty() {
            return cached;
        }

        let mut merged = cached;
        for chunk in missing.chunks(8) {
            let requests = chunk.iter().map(|id| {
                let http = self.http.clone();
                let cookie = cookie.to_string();
                let id = id.clone();
                async move {
                    let response = http
                        .get(format!("{BASE}worlds/{id}"))
                        .header(reqwest::header::COOKIE, format!("auth={cookie}"))
                        .send()
                        .await;
                    let name = match response {
                        Ok(response) if response.status().is_success() => response
                            .json::<Value>()
                            .await
                            .ok()
                            .and_then(|body| {
                                body.get("name").and_then(Value::as_str).map(str::to_string)
                            })
                            .unwrap_or_default(),
                        _ => String::new(),
                    };
                    (id, name)
                }
            });
            for (id, name) in join_all(requests).await {
                merged.insert(id, name);
            }
        }

        let gained = missing
            .iter()
            .any(|id| merged.get(id).is_some_and(|name| !name.is_empty()));
        if let Ok(mut guard) = self.worlds.lock() {
            *guard = merged.clone();
        }
        if gained {
            let now = now_secs();
            if let Ok(mut stamps) = self.world_seen.lock() {
                for id in &missing {
                    if merged.get(id).is_some_and(|name| !name.is_empty()) {
                        stamps.insert(id.clone(), now);
                    }
                }
                // Sized to the roster the window is showing; without one (the
                // headless dump) the hard ceiling applies instead of a guess.
                let friends = self
                    .roster
                    .lock()
                    .ok()
                    .and_then(|guard| guard.as_ref().map(|roster| roster.items.len()));
                let cap = friends.map(world_cache_cap).unwrap_or(WORLD_CACHE_MAX);
                if self.persist {
                    if let Some(account) = self.current_account() {
                        save_world_cache(&world_cache_path(&account), &merged, &stamps, cap);
                    }
                }
            }
        }
        merged
    }

    // -------------------------------------------------------------- realtime

    pub fn start_realtime(self: &Arc<Self>) {
        if self.realtime_started.swap(true, Ordering::SeqCst) {
            return;
        }
        let api = self.clone();
        tokio::spawn(async move { api.realtime_loop().await });
    }

    pub fn live(&self) -> bool {
        self.live.load(Ordering::SeqCst)
    }

    pub fn presence_summary(&self) -> (usize, usize, Vec<String>) {
        let presence = self
            .presence
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default();
        let online = presence.values().filter(|entry| entry.online).count();
        let sample = presence
            .values()
            .take(4)
            .map(|entry| format!("{}|{}", entry.location, entry.status))
            .collect();
        (presence.len(), online, sample)
    }

    pub fn event_counts(&self) -> HashMap<String, u32> {
        self.events
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default()
    }

    pub fn last_error(&self) -> String {
        self.realtime_error
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default()
    }

    fn set_error(&self, message: String) {
        if let Ok(mut guard) = self.realtime_error.lock() {
            *guard = message;
        }
    }

    async fn realtime_token(&self) -> Result<String, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, text) = self
            .request(reqwest::Method::GET, "auth", Some(&cookie), None)
            .await?;
        if !status.is_success() {
            return Err("VRChat refused a realtime token.".to_string());
        }
        let body: Value = serde_json::from_str(&text).map_err(|_| "bad token body".to_string())?;
        body.get("token")
            .and_then(Value::as_str)
            .map(str::to_string)
            .filter(|token| !token.is_empty())
            .ok_or_else(|| "VRChat returned no realtime token.".to_string())
    }

    async fn realtime_loop(self: Arc<Self>) {
        let mut backoff = 2u64;
        loop {
            if self.cookie().is_none() {
                self.live.store(false, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_secs(3)).await;
                continue;
            }

            let token = match self.realtime_token().await {
                Ok(token) => token,
                Err(error) => {
                    self.set_error(format!("token: {error}"));
                    self.live.store(false, Ordering::SeqCst);
                    tokio::time::sleep(Duration::from_secs(backoff)).await;
                    backoff = (backoff * 2).min(30);
                    continue;
                }
            };

            let encoded: String = url::form_urlencoded::byte_serialize(token.as_bytes()).collect();
            let url = format!("{PIPELINE}/?auth={encoded}");

            let request = match url.as_str().into_client_request() {
                Ok(mut request) => {
                    let headers = request.headers_mut();
                    headers.insert(
                        "Origin",
                        tokio_tungstenite::tungstenite::http::HeaderValue::from_static(
                            "https://vrchat.com",
                        ),
                    );
                    headers.insert(
                        "User-Agent",
                        tokio_tungstenite::tungstenite::http::HeaderValue::from_static(USER_AGENT),
                    );
                    request
                }
                Err(error) => {
                    self.set_error(format!("request: {error}"));
                    tokio::time::sleep(Duration::from_secs(5)).await;
                    continue;
                }
            };

            match tokio_tungstenite::connect_async(request).await {
                Ok((socket, _)) => {
                    backoff = 2;
                    self.live.store(true, Ordering::SeqCst);
                    let (mut write, mut read) = socket.split();
                    let keepalive = tokio::spawn(async move {
                        loop {
                            tokio::time::sleep(Duration::from_secs(20)).await;
                            if write.send(Message::Ping(Vec::new())).await.is_err() {
                                break;
                            }
                        }
                    });

                    while let Some(message) = read.next().await {
                        match message {
                            Ok(Message::Text(text)) => self.handle_realtime(&text),
                            Ok(Message::Close(_)) | Err(_) => break,
                            _ => {}
                        }
                    }

                    keepalive.abort();
                    self.live.store(false, Ordering::SeqCst);
                }
                Err(error) => {
                    self.set_error(format!("connect: {error}"));
                    self.live.store(false, Ordering::SeqCst);
                }
            }

            tokio::time::sleep(Duration::from_secs(backoff)).await;
            backoff = (backoff * 2).min(30);
        }
    }

    fn handle_realtime(self: &Arc<Self>, raw: &str) {
        let Some(event) = parse_pipeline_event(raw) else {
            return;
        };
        if let Ok(mut counts) = self.events.lock() {
            *counts.entry(event.kind.clone()).or_insert(0) += 1;
        }

        match event.kind.as_str() {
            "user-location" | "user-update" => {
                if !event.location.is_empty() {
                    if let Ok(mut location) = self.self_location.lock() {
                        *location = event.location.clone();
                    }
                }
            }
            "friend-offline" => {
                if let Ok(mut presence) = self.presence.lock() {
                    let entry = presence.entry(event.user_id.clone()).or_default();
                    entry.location = "offline".to_string();
                    entry.online = false;
                }
                self.sync_presence_row(&event);
                self.record_activity(1, &event, "went offline");
            }
            "friend-online" | "friend-active" | "friend-location" | "friend-update" => {
                let mut previous_avatar = String::new();
                if let Ok(mut presence) = self.presence.lock() {
                    let entry = presence.entry(event.user_id.clone()).or_default();
                    previous_avatar = entry.avatar.clone();
                    if !event.location.is_empty() {
                        entry.online = event.location != "offline";
                        entry.location = event.location.clone();
                    }
                    if !event.status.is_empty() {
                        entry.status = event.status.clone();
                    }
                    if !event.status_description.is_empty() {
                        entry.status_description = event.status_description.clone();
                    }
                    if !event.avatar.is_empty() {
                        entry.avatar = event.avatar.clone();
                    }
                }

                if event.kind == "friend-online" {
                    self.record_activity(0, &event, "came online");
                }
                if !event.avatar.is_empty()
                    && !previous_avatar.is_empty()
                    && previous_avatar != event.avatar
                {
                    self.record_activity(13, &event, "changed avatar");
                }
                self.sync_presence_row(&event);
            }
            "friend-delete" => {
                if let Ok(mut presence) = self.presence.lock() {
                    presence.remove(&event.user_id);
                }
            }
            _ => {}
        }
    }

    /// Pushes a presence change into the stored roster row and tells the UI, so
    /// online rows move without waiting for the next full roster refresh.
    fn sync_presence_row(self: &Arc<Self>, event: &PipelineEvent) {
        let entry = self
            .presence
            .lock()
            .ok()
            .and_then(|guard| guard.get(&event.user_id).cloned());
        let Some(entry) = entry else {
            return;
        };
        let worlds = self.world_names();

        let (friend, online, total, unknown_world) = {
            let Ok(mut guard) = self.roster.lock() else {
                return;
            };
            let Some(roster) = guard.as_mut() else {
                return;
            };
            let Some(row) = roster
                .items
                .iter_mut()
                .find(|friend| friend.id == event.user_id)
            else {
                // The friend is not on the loaded pages yet; the next walk will
                // pick the row up with this presence overlay applied.
                return;
            };

            row.location = entry.location.clone();
            if !entry.status.is_empty() {
                row.status = entry.status.clone();
            }
            if !entry.status_description.is_empty() {
                row.status_description = entry.status_description.clone();
            }
            if !entry.avatar.is_empty() {
                row.avatar = entry.avatar.clone();
            }
            locate(row, &worlds);

            // The row borrow ends here so the roster can be recounted.
            let friend = row.clone();
            // Only an unnamed world is worth a lookup.
            let unknown_world = world_id_of(&friend.location)
                .filter(|id| !worlds.contains_key(id));
            if let Some(id) = &unknown_world {
                if !roster.missing_worlds.contains(id) {
                    roster.missing_worlds.push(id.clone());
                }
            }
            roster.worlds_pending = roster
                .missing_worlds
                .iter()
                .filter(|id| !worlds.contains_key(*id))
                .count();
            roster.online = roster.items.iter().filter(|friend| friend.online).count();
            (friend, roster.online, roster.total, unknown_world)
        };

        if let Some(id) = unknown_world {
            self.resolve_worlds_in_background(vec![id]);
        }
        let app = self.app.lock().ok().and_then(|guard| guard.clone());
        if let Some(app) = app {
            use tauri::Emitter;
            let _ = app.emit(
                "friend-presence",
                PresenceUpdate {
                    friend,
                    online,
                    total,
                },
            );
        }
    }

    fn record_activity(&self, kind: u32, event: &PipelineEvent, details: &str) {
        let timestamp = crate::activity::now_iso();
        self.activity.record(&crate::activity::LoggedEvent {
            id: format!("{kind}-{}-{timestamp}", event.user_id),
            kind,
            user_id: event.user_id.clone(),
            display_name: event.display_name.clone(),
            avatar: event.avatar.clone(),
            location: event.location.clone(),
            timestamp,
            details: details.to_string(),
        });
    }

    // -------------------------------------------------------------- instance

    // --------------------------------------------------------------- actions

    pub async fn invite_friend(&self, user_id: &str) -> Result<(), String> {
        let user_id = path_id(user_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let instance = invite_instance_from(&self.location())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::POST,
                &format!("invite/{user_id}"),
                Some(&cookie),
                Some(serde_json::json!({ "instanceId": instance, "messageSlot": 0 })),
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    pub async fn request_invite(&self, user_id: &str) -> Result<(), String> {
        let user_id = path_id(user_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::POST,
                &format!("requestInvite/{user_id}"),
                Some(&cookie),
                Some(serde_json::json!({ "requestSlot": 0 })),
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    pub async fn friend_favorites(&self) -> Result<HashMap<String, String>, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::GET,
                "favorites?type=friend",
                Some(&cookie),
                None,
            )
            .await?;
        if !status.is_success() {
            return Err(api_message(status, &body_text));
        }
        Ok(friend_favorites_from(&body_text))
    }

    pub async fn add_friend_favorite(&self, user_id: &str) -> Result<(), String> {
        let user_id = path_id(user_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::POST,
                "favorites",
                Some(&cookie),
                Some(serde_json::json!({
                    "type": "friend",
                    "favoriteId": user_id,
                    "tags": ["group_0"],
                })),
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    pub async fn remove_friend_favorite(&self, favorite_id: &str) -> Result<(), String> {
        let favorite_id = path_id(favorite_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::DELETE,
                &format!("favorites/{favorite_id}"),
                Some(&cookie),
                None,
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    pub async fn accept_friend_request(&self, notification_id: &str) -> Result<(), String> {
        let notification_id = path_id(notification_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::PUT,
                &format!("auth/user/notifications/{notification_id}/accept"),
                Some(&cookie),
                None,
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    pub async fn hide_notification(&self, notification_id: &str) -> Result<(), String> {
        let notification_id = path_id(notification_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::PUT,
                &format!("auth/user/notifications/{notification_id}/hide"),
                Some(&cookie),
                None,
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    /// Sends this account an invite to the instance the notification points at,
    /// so the game offers it; the shell cannot join a world by itself.
    pub async fn invite_myself_to(&self, location: &str) -> Result<(), String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let instance = invite_instance_from(location)?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::POST,
                &format!("invite/myself/to/{instance}"),
                Some(&cookie),
                None,
            )
            .await?;
        if status.is_success() {
            Ok(())
        } else {
            Err(api_message(status, &body_text))
        }
    }

    pub fn location(&self) -> String {
        self.self_location
            .lock()
            .map(|guard| guard.clone())
            .unwrap_or_default()
    }

    pub async fn instance(&self) -> Result<InstanceView, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let location = self.location();
        if !location.starts_with("wrld_") {
            return Ok(InstanceView {
                in_instance: false,
                location,
                world_id: String::new(),
                world_name: String::new(),
                world_thumbnail: String::new(),
                instance_type: String::new(),
                capacity: 0,
                players: Vec::new(),
                notice: String::new(),
            });
        }

        // The instance endpoint wants `world:instance` without the trailing
        // `~region(...)` style tags the location string carries.
        let trimmed = location.split('~').next().unwrap_or(&location).to_string();
        let world_id = trimmed.split(':').next().unwrap_or_default().to_string();
        let instance_type = instance_label(&location);
        let (status, _, body_text) = self
            .request(
                reqwest::Method::GET,
                &format!("instances/{trimmed}"),
                Some(&cookie),
                None,
            )
            .await?;

        if !status.is_success() {
            let worlds = self.resolve_worlds(vec![world_id.clone()], &cookie).await;
            return Ok(InstanceView {
                in_instance: true,
                location,
                world_name: worlds.get(&world_id).cloned().unwrap_or_default(),
                world_id,
                world_thumbnail: String::new(),
                instance_type,
                capacity: 0,
                players: Vec::new(),
                notice: match status.as_u16() {
                    403 => "VRChat hides the player list for this instance.".to_string(),
                    404 => "That instance is no longer available.".to_string(),
                    code => format!("VRChat returned {code}."),
                },
            });
        }

        let body: Value = serde_json::from_str(&body_text)
            .map_err(|_| "VRChat returned an unexpected instance payload.".to_string())?;
        Ok(parse_instance_body(&location, &body))
    }

    // ------------------------------------------------------- favorite details

    /// Name and thumbnail for favorite avatars, filling the shell's cache.
    /// Returns the details that were found plus the ids VRChat rejected, so the
    /// caller can stop retrying deleted or hidden favorites.
    pub async fn avatar_details(
        &self,
        ids: &[String],
    ) -> (Vec<(String, String, String)>, Vec<String>) {
        self.details_for("avatars", ids).await
    }

    pub async fn world_details(
        &self,
        ids: &[String],
    ) -> (Vec<(String, String, String)>, Vec<String>) {
        self.details_for("worlds", ids).await
    }

    /// Sequential lookups with a small gap keep the rate limit happy; callers
    /// only ask for a few ids at a time and cache the answers. Transport errors
    /// are left out of both lists so the id is retried on a later pass.
    async fn details_for(
        &self,
        collection: &str,
        ids: &[String],
    ) -> (Vec<(String, String, String)>, Vec<String>) {
        let mut found = Vec::new();
        let mut rejected = Vec::new();
        let Some(cookie) = self.cookie() else {
            return (found, rejected);
        };

        for (index, id) in ids.iter().take(5).enumerate() {
            if index > 0 {
                tokio::time::sleep(Duration::from_millis(350)).await;
            }
            let Ok((status, _, body)) = self
                .request(
                    reqwest::Method::GET,
                    &format!("{collection}/{id}"),
                    Some(&cookie),
                    None,
                )
                .await
            else {
                continue;
            };
            if !status.is_success() {
                if matches!(status.as_u16(), 403 | 404 | 410) {
                    rejected.push(id.clone());
                }
                continue;
            }
            let Ok(value) = serde_json::from_str::<Value>(&body) else {
                continue;
            };
            let name = text(&value, "name");
            if name.is_empty() {
                rejected.push(id.clone());
                continue;
            }
            let thumbnail = {
                let candidate = text(&value, "thumbnailImageUrl");
                if candidate.is_empty() {
                    text(&value, "imageUrl")
                } else {
                    candidate
                }
            };
            found.push((id.clone(), name, thumbnail));
        }
        (found, rejected)
    }

    // --------------------------------------------------------- notifications

    pub async fn notifications(&self) -> Result<Vec<Notice>, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::GET,
                "auth/user/notifications?n=100",
                Some(&cookie),
                None,
            )
            .await?;
        if status == reqwest::StatusCode::UNAUTHORIZED {
            return Err("Your session expired. Sign in again.".to_string());
        }
        if !status.is_success() {
            return Err(format!(
                "VRChat returned {} while loading notifications.",
                status.as_u16()
            ));
        }
        let rows: Vec<Value> = serde_json::from_str(&body_text).unwrap_or_default();
        Ok(rows.iter().map(notice_from).collect())
    }

    /// One notification, straight from VRChat by id. Accepting works from this
    /// record rather than from whatever kind or instance the window sent.
    pub async fn notification(&self, notification_id: &str) -> Result<Notice, String> {
        let id = path_id(notification_id)?;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let (status, _, body_text) = self
            .request(
                reqwest::Method::GET,
                &format!("auth/user/notifications/{id}"),
                Some(&cookie),
                None,
            )
            .await?;
        if status == reqwest::StatusCode::NOT_FOUND {
            return Err("That notification is no longer waiting.".to_string());
        }
        if !status.is_success() {
            return Err(api_message(status, &body_text));
        }
        let value: Value = serde_json::from_str(&body_text)
            .map_err(|_| "VRChat returned an unexpected notification.".to_string())?;
        Ok(notice_from(&value))
    }

    /// One page of notifications plus the waiting total. The page is the
    /// request this call already makes; the total rides along by tracking the
    /// first page's ids and only walking the whole list occasionally.
    pub async fn notification_overview(&self, offset: usize) -> Result<NoticeOverview, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let offset = offset.min(NOTICE_MAX_OFFSET);
        let (status, _, body_text) = self
            .request(
                reqwest::Method::GET,
                &format!("auth/user/notifications?n={NOTICE_PAGE}&offset={offset}"),
                Some(&cookie),
                None,
            )
            .await?;
        if status == reqwest::StatusCode::UNAUTHORIZED {
            return Err("Your session expired. Sign in again.".to_string());
        }
        if !status.is_success() {
            return Err(format!(
                "VRChat returned {} while loading notifications.",
                status.as_u16()
            ));
        }
        self.notice_pages.fetch_add(1, Ordering::SeqCst);
        let rows: Vec<Value> = serde_json::from_str(&body_text).unwrap_or_default();
        let has_more = page_has_more(rows.len());
        let waiting = if offset == 0 {
            self.waiting_notices(&rows).await?
        } else {
            self.notice_count
                .lock()
                .ok()
                .and_then(|guard| guard.as_ref().map(|count| count.ids.len()))
                .unwrap_or(0)
        };

        Ok(NoticeOverview {
            notices: rows.iter().map(notice_from).collect(),
            waiting,
            offset,
            has_more,
        })
    }

    /// The waiting total, from the first page plus a cached count. The whole
    /// list is walked only on the first call, after a handle changes it, or
    /// every half hour to correct for requests answered elsewhere.
    async fn waiting_notices(&self, first_page: &[Value]) -> Result<usize, String> {
        let now = now_secs();
        let current = actionable_ids(first_page);
        if let Some(mut cached) = self
            .notice_count
            .lock()
            .ok()
            .and_then(|guard| guard.clone())
        {
            if now.saturating_sub(cached.at) < NOTICE_RECOUNT_SECS {
                merge_notice_page(&mut cached, current);
                // Spend a few requests on ids that just left the first page:
                // gone means answered elsewhere, still there means pushed down.
                let mut checked = 0;
                while checked < NOTICE_CHECK_BUDGET {
                    let Some(id) = cached.checks.pop_front() else {
                        break;
                    };
                    match self.notification_alive(&id).await {
                        Some(alive) => {
                            self.notice_checks.fetch_add(1, Ordering::SeqCst);
                            settle_notice_check(&mut cached, &id, alive);
                            checked += 1;
                        }
                        None => {
                            // Transport trouble: keep it queued for next time.
                            cached.checks.push_front(id);
                            break;
                        }
                    }
                }
                let total = cached.ids.len();
                if let Ok(mut guard) = self.notice_count.lock() {
                    *guard = Some(cached.clone());
                }
                return Ok(total);
            }
        }
        self.notice_walks.fetch_add(1, Ordering::SeqCst);
        let total = self.walk_notice_count(first_page).await?;
        Ok(total)
    }

    /// Whether one notification still exists, without hiding it; `None` means
    /// the answer was inconclusive and the check should be retried.
    async fn notification_alive(&self, notification_id: &str) -> Option<bool> {
        let cookie = self.cookie()?;
        let Ok((status, _, _)) = self
            .request(
                reqwest::Method::GET,
                &format!("auth/user/notifications/{notification_id}"),
                Some(&cookie),
                None,
            )
            .await
        else {
            return None;
        };
        match status.as_u16() {
            200..=299 => Some(true),
            404 | 410 => Some(false),
            _ => None,
        }
    }

    async fn walk_notice_count(&self, first_page: &[Value]) -> Result<usize, String> {
        const MAX_PAGES: usize = 20;
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let mut ids = actionable_ids(first_page);
        let mut offset = first_page.len();
        let mut more = page_has_more(first_page.len());

        for _ in 1..MAX_PAGES {
            if !more {
                break;
            }
            let (status, _, body_text) = self
                .request(
                    reqwest::Method::GET,
                    &format!("auth/user/notifications?n={NOTICE_PAGE}&offset={offset}"),
                    Some(&cookie),
                    None,
                )
                .await?;
            if !status.is_success() {
                break;
            }
            self.notice_pages.fetch_add(1, Ordering::SeqCst);
            let rows: Vec<Value> = serde_json::from_str(&body_text).unwrap_or_default();
            ids.extend(actionable_ids(&rows));
            offset += rows.len();
            more = page_has_more(rows.len());
        }

        let total = ids.len();
        if let Ok(mut guard) = self.notice_count.lock() {
            *guard = Some(NoticeCount {
                page_ids: actionable_ids(first_page),
                ids,
                checks: VecDeque::new(),
                at: now_secs(),
            });
        }
        Ok(total)
    }

    /// Pages fetched for notifications so far, and how many full counts ran.
    pub fn notice_stats(&self) -> HashMap<String, u32> {
        HashMap::from([
            (
                "pages".to_string(),
                self.notice_pages.load(Ordering::SeqCst),
            ),
            (
                "walks".to_string(),
                self.notice_walks.load(Ordering::SeqCst),
            ),
            (
                "checks".to_string(),
                self.notice_checks.load(Ordering::SeqCst),
            ),
        ])
    }

    /// A handled notification makes the cached count wrong; recount next time.
    fn invalidate_notice_count(&self) {
        if let Ok(mut guard) = self.notice_count.lock() {
            *guard = None;
        }
    }

    pub async fn accept_notification(&self, notification_id: &str) -> Result<NoticeOverview, String> {
        let id = path_id(notification_id)?;
        let notice = self.notification(&id).await?;
        match notice.kind.as_str() {
            "friendRequest" => self.accept_friend_request(&id).await?,
            "invite" => self.invite_myself_to(&notice.location).await?,
            "requestInvite" => self.invite_friend(&notice.sender_id).await?,
            other => {
                return Err(format!(
                    "There is nothing to accept on a {other} notification."
                ))
            }
        }
        self.hide_notification(&id).await?;
        self.invalidate_notice_count();
        self.notification_overview(0).await
    }

    pub async fn dismiss_notification(
        &self,
        notification_id: &str,
    ) -> Result<NoticeOverview, String> {
        let id = path_id(notification_id)?;
        // Confirm the row is still there before hiding anything.
        let _ = self.notification(&id).await?;
        self.hide_notification(&id).await?;
        self.invalidate_notice_count();
        self.notification_overview(0).await
    }

    // ----------------------------------------------------------------- image

    /// Proxies a VRChat image with the session cookie attached. Private avatar
    /// thumbnails are only served to an authenticated caller, and a WebView
    /// `<img>` cannot send that cookie itself.
    pub async fn image(&self, url: &str) -> Result<(String, Vec<u8>), String> {
        let parsed = url::Url::parse(url).map_err(|_| "Bad image URL.".to_string())?;
        let host = parsed.host_str().unwrap_or_default();
        if !host.ends_with("vrchat.cloud") {
            return Err("Image host is not allowed.".to_string());
        }

        let mut request = self.http.get(parsed);
        if let Some(cookie) = self.cookie() {
            request = request.header(reqwest::header::COOKIE, format!("auth={cookie}"));
        }
        let response = request.send().await.map_err(network_error)?;
        if !response.status().is_success() {
            return Err(format!("image status {}", response.status().as_u16()));
        }
        let mime = response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .unwrap_or("image/png")
            .to_string();
        let bytes = response.bytes().await.map_err(network_error)?.to_vec();
        Ok((mime, bytes))
    }
}

fn text(value: &Value, key: &str) -> String {
    value
        .get(key)
        .and_then(Value::as_str)
        .unwrap_or_default()
        .to_string()
}

fn pick(primary: &Value, fallback: &Value, key: &str) -> String {
    let value = text(primary, key);
    if value.is_empty() {
        text(fallback, key)
    } else {
        value
    }
}

fn display_name(value: &Value) -> String {
    let name = text(value, "displayName");
    if name.is_empty() {
        text(value, "username")
    } else {
        name
    }
}

fn pick_avatar(value: &Value) -> String {
    for key in [
        "currentAvatarThumbnailImageUrl",
        "currentAvatarImageUrl",
        "profilePicOverrideThumbnail",
        "profilePicOverride",
        "userIcon",
        "iconUrl",
    ] {
        let candidate = text(value, key);
        if !candidate.is_empty() {
            return candidate;
        }
    }
    String::new()
}

fn trust_label(value: &Value) -> String {
    let tags = value
        .get("tags")
        .and_then(Value::as_array)
        .cloned()
        .unwrap_or_default();
    let has = |tag: &str| tags.iter().any(|candidate| candidate.as_str() == Some(tag));
    if has("system_trust_veteran") {
        "Trusted".to_string()
    } else if has("system_trust_trusted") {
        "Known".to_string()
    } else if has("system_trust_known") || has("system_trust_intermediate") {
        "User".to_string()
    } else if has("system_trust_basic") {
        "New".to_string()
    } else {
        String::new()
    }
}

fn instance_label(location: &str) -> String {
    let Some((_, rest)) = location.split_once(':') else {
        return String::new();
    };
    if rest.is_empty() {
        return String::new();
    }
    if rest.contains("~hidden") {
        "friends+".to_string()
    } else if rest.contains("~friends") {
        "friends".to_string()
    } else if rest.contains("~private") {
        "private".to_string()
    } else if rest.contains("~canRequestInvite") {
        "invite+".to_string()
    } else {
        "public".to_string()
    }
}

/// Recomputes the fields derived from rows after they were added or relabelled.
fn refresh_counts(roster: &mut Roster, worlds: &HashMap<String, String>) {
    roster.total = roster.items.len();
    roster.online = roster.items.iter().filter(|friend| friend.online).count();
    roster.missing_worlds = missing_worlds(&roster.items, worlds);
    roster.worlds_pending = roster.missing_worlds.len();
}

/// World ids the rows cannot name yet, in first-seen order.
fn missing_worlds(items: &[Friend], worlds: &HashMap<String, String>) -> Vec<String> {
    let mut seen: HashSet<String> = HashSet::new();
    items
        .iter()
        .filter_map(|friend| world_id_of(&friend.location))
        .filter(|id| !worlds.contains_key(id))
        .filter(|id| seen.insert(id.clone()))
        .collect()
}

fn world_id_of(location: &str) -> Option<String> {
    if !location.starts_with("wrld_") {
        return None;
    }
    location.split(':').next().map(str::to_string)
}

fn locate(friend: &mut Friend, worlds: &HashMap<String, String>) {
    let location = friend.location.clone();
    friend.online = !location.is_empty() && location != "offline";

    let (world, instance) = if location.starts_with("wrld_") {
        let world_id = location.split(':').next().unwrap_or_default();
        let name = worlds
            .get(world_id)
            .cloned()
            .filter(|name| !name.is_empty())
            .unwrap_or_else(|| world_id.to_string());
        (name, instance_label(&location))
    } else if location == "private" {
        ("Private world".to_string(), "private".to_string())
    } else if location == "traveling" {
        ("Traveling".to_string(), String::new())
    } else {
        (String::new(), String::new())
    };

    friend.world = world;
    friend.instance = instance;
}

fn user_info(value: &Value) -> UserInfo {
    UserInfo {
        id: text(value, "id"),
        display_name: display_name(value),
        avatar: pick_avatar(value),
        status: text(value, "status"),
        trust: trust_label(value),
        location: own_location(value),
    }
}

/// `auth/user` no longer carries a top-level `location`; the account's own
/// presence lives in `presence.world` + `presence.instance`.
fn own_location(value: &Value) -> String {
    let Some(presence) = value.get("presence") else {
        return text(value, "location");
    };
    let world = text(presence, "world");
    let instance = text(presence, "instance");
    if world.is_empty() || instance.is_empty() {
        text(value, "location")
    } else {
        format!("{world}:{instance}")
    }
}

/// A pipeline frame reduced to the fields the roster and activity log need.
#[derive(Debug, PartialEq, Clone)]
pub(crate) struct PipelineEvent {
    pub kind: String,
    pub user_id: String,
    pub location: String,
    pub status: String,
    pub status_description: String,
    pub avatar: String,
    pub display_name: String,
}

/// The pipeline sends `content` either as an object or as a JSON string,
/// sometimes with the user nested under `user`; all three shapes are real.
pub(crate) fn parse_pipeline_event(raw: &str) -> Option<PipelineEvent> {
    let message: Value = serde_json::from_str(raw).ok()?;
    let kind = message.get("type").and_then(Value::as_str)?.to_string();
    let content = match message.get("content") {
        Some(Value::String(inner)) => serde_json::from_str::<Value>(inner).unwrap_or(Value::Null),
        Some(value) => value.clone(),
        None => Value::Null,
    };
    let embedded = content.get("user").cloned().unwrap_or(Value::Null);
    let user_id = {
        let top = text(&content, "userId");
        if top.is_empty() {
            text(&embedded, "id")
        } else {
            top
        }
    };
    if user_id.is_empty() {
        return None;
    }

    let thumbnail = pick(&content, &embedded, "currentAvatarThumbnailImageUrl");
    let avatar = if thumbnail.is_empty() {
        pick(&content, &embedded, "currentAvatarImageUrl")
    } else {
        thumbnail
    };

    Some(PipelineEvent {
        kind,
        user_id,
        location: pick(&content, &embedded, "location"),
        status: pick(&content, &embedded, "status"),
        status_description: pick(&content, &embedded, "statusDescription"),
        avatar,
        display_name: pick(&content, &embedded, "displayName"),
    })
}

fn map_friend(value: &Value, worlds: &HashMap<String, String>) -> Friend {
    let mut friend = Friend {
        id: text(value, "id"),
        name: display_name(value),
        status: text(value, "status"),
        status_description: text(value, "statusDescription"),
        location: text(value, "location"),
        world: String::new(),
        instance: String::new(),
        avatar: pick_avatar(value),
        online: false,
        trust: trust_label(value),
    };
    locate(&mut friend, worlds);
    friend
}

fn auth_cookie(headers: &reqwest::header::HeaderMap) -> Option<String> {
    for value in headers.get_all(reqwest::header::SET_COOKIE) {
        let Ok(raw) = value.to_str() else { continue };
        let first = raw.split(';').next().unwrap_or_default().trim();
        if let Some(cookie) = first.strip_prefix("auth=") {
            let cookie = cookie.trim_matches('"').trim();
            if !cookie.is_empty() {
                return Some(cookie.to_string());
            }
        }
    }
    None
}

fn two_factor_methods(body: &Value) -> Option<Vec<String>> {
    let methods = body.get("requiresTwoFactorAuth")?.as_array()?;
    Some(
        methods
            .iter()
            .filter_map(|method| method.as_str().map(str::to_string))
            .collect(),
    )
}

fn login_error(status: reqwest::StatusCode, body: &Value) -> String {
    match status.as_u16() {
        401 => "That username or password was rejected.".to_string(),
        403 => "VRChat refused the sign-in attempt.".to_string(),
        429 => "VRChat is rate limiting sign-ins. Wait a minute and try again.".to_string(),
        code => {
            let message = body
                .get("error")
                .and_then(|error| error.get("message"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            if message.is_empty() {
                format!("VRChat returned {code}.")
            } else {
                message.to_string()
            }
        }
    }
}

fn network_error(error: reqwest::Error) -> String {
    if error.is_timeout() {
        "VRChat timed out. Check the connection and try again.".to_string()
    } else if error.is_connect() {
        "Could not reach VRChat. Check the connection.".to_string()
    } else {
        format!("Network error: {error}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use reqwest::header::{HeaderMap, HeaderValue, SET_COOKIE};

    fn cookie_headers(values: &[&str]) -> HeaderMap {
        let mut headers = HeaderMap::new();
        for value in values {
            headers.append(SET_COOKIE, HeaderValue::from_str(value).expect("valid header"));
        }
        headers
    }

    fn friend(location: &str) -> Friend {
        Friend {
            id: "usr_1".to_string(),
            name: "One".to_string(),
            status: "active".to_string(),
            status_description: String::new(),
            location: location.to_string(),
            world: String::new(),
            instance: String::new(),
            avatar: String::new(),
            online: false,
            trust: String::new(),
        }
    }

    #[test]
    fn reads_the_auth_cookie_beside_other_cookies() {
        let headers = cookie_headers(&[
            "theme=dark; Path=/",
            "auth=abc123; Path=/; HttpOnly",
            "twoFactorAuth=xyz; Path=/",
        ]);
        assert_eq!(auth_cookie(&headers), Some("abc123".to_string()));
    }

    #[test]
    fn ignores_headers_without_an_auth_cookie() {
        let headers = cookie_headers(&["theme=dark; Path=/", "auth=; Path=/"]);
        assert_eq!(auth_cookie(&headers), None);
        assert_eq!(auth_cookie(&HeaderMap::new()), None);
    }

    #[test]
    fn trims_quoted_auth_cookie_values() {
        let headers = cookie_headers(&["auth=\"quoted-value\"; Path=/"]);
        assert_eq!(auth_cookie(&headers), Some("quoted-value".to_string()));
    }

    #[test]
    fn detects_two_factor_methods() {
        let body: Value = serde_json::json!({ "requiresTwoFactorAuth": ["totp", "otp"] });
        assert_eq!(
            two_factor_methods(&body),
            Some(vec!["totp".to_string(), "otp".to_string()])
        );
    }

    #[test]
    fn finds_no_two_factor_without_the_field() {
        let body: Value = serde_json::json!({ "id": "usr_1", "displayName": "One" });
        assert_eq!(two_factor_methods(&body), None);
    }

    #[test]
    fn parses_object_content() {
        let raw = r#"{"type":"friend-location","content":{"userId":"usr_2","location":"wrld_a:1~hidden(u)","status":"join me","statusDescription":"hi","displayName":"Two"}}"#;
        let event = parse_pipeline_event(raw).expect("event");
        assert_eq!(event.kind, "friend-location");
        assert_eq!(event.user_id, "usr_2");
        assert_eq!(event.location, "wrld_a:1~hidden(u)");
        assert_eq!(event.status, "join me");
        assert_eq!(event.status_description, "hi");
        assert_eq!(event.display_name, "Two");
    }

    #[test]
    fn parses_string_content() {
        let raw = r#"{"type":"friend-online","content":"{\"userId\":\"usr_3\",\"location\":\"wrld_b:2\",\"currentAvatarThumbnailImageUrl\":\"https://api.vrchat.cloud/thumb.png\"}"}"#;
        let event = parse_pipeline_event(raw).expect("event");
        assert_eq!(event.kind, "friend-online");
        assert_eq!(event.user_id, "usr_3");
        assert_eq!(event.location, "wrld_b:2");
        assert_eq!(event.avatar, "https://api.vrchat.cloud/thumb.png");
    }

    #[test]
    fn reads_the_id_from_the_embedded_user() {
        let raw = r#"{"type":"friend-update","content":{"user":{"id":"usr_4","displayName":"Four","currentAvatarImageUrl":"https://api.vrchat.cloud/a.png"}}}"#;
        let event = parse_pipeline_event(raw).expect("event");
        assert_eq!(event.user_id, "usr_4");
        assert_eq!(event.display_name, "Four");
        assert_eq!(event.avatar, "https://api.vrchat.cloud/a.png");
    }

    #[test]
    fn offline_events_parse_without_a_location() {
        let raw = r#"{"type":"friend-offline","content":{"userId":"usr_5"}}"#;
        let event = parse_pipeline_event(raw).expect("event");
        assert_eq!(event.kind, "friend-offline");
        assert_eq!(event.location, "");
    }

    #[test]
    fn rejects_frames_without_an_id_or_type() {
        assert!(parse_pipeline_event(r#"{"type":"friend-online","content":{}}"#).is_none());
        assert!(parse_pipeline_event(r#"{"content":{"userId":"usr_1"}}"#).is_none());
        assert!(parse_pipeline_event("not json").is_none());
    }

    #[test]
    fn online_friends_come_from_the_online_bucket_first() {
        let online = serde_json::json!({ "id": "usr_a", "displayName": "A", "location": "wrld_x:1~friends(usr_a)" });
        let offline_duplicate = serde_json::json!({ "id": "usr_a", "displayName": "A", "location": "offline" });
        let offline_only = serde_json::json!({ "id": "usr_b", "displayName": "B", "location": "offline" });
        let broken = serde_json::json!({ "displayName": "no id" });

        let mut users = Vec::new();
        let mut seen = HashSet::new();
        push_unique(&mut users, &mut seen, broken);
        push_unique(&mut users, &mut seen, online);
        push_unique(&mut users, &mut seen, offline_duplicate);
        push_unique(&mut users, &mut seen, offline_only);

        assert_eq!(users.len(), 2);
        assert_eq!(text(&users[0], "location"), "wrld_x:1~friends(usr_a)");
        assert_eq!(text(&users[1], "id"), "usr_b");
    }

    #[test]
    fn reads_the_account_location_from_presence() {
        let body: Value = serde_json::json!({
            "id": "usr_1",
            "presence": {
                "world": "wrld_x",
                "instance": "12345~hidden(usr_9)",
                "instanceType": "hidden"
            }
        });
        assert_eq!(own_location(&body), "wrld_x:12345~hidden(usr_9)");
    }

    #[test]
    fn falls_back_to_the_location_field_when_presence_is_empty() {
        let offline: Value =
            serde_json::json!({ "id": "usr_1", "presence": { "world": "", "instance": "" } });
        assert_eq!(own_location(&offline), "");

        let legacy: Value = serde_json::json!({ "id": "usr_1", "location": "offline" });
        assert_eq!(own_location(&legacy), "offline");
    }

    #[test]
    fn maps_instance_types() {
        assert_eq!(instance_label("wrld_a:123~hidden(usr_1)"), "friends+");
        assert_eq!(instance_label("wrld_a:123~friends(usr_1)"), "friends");
        assert_eq!(
            instance_label("wrld_a:123~private(usr_1)~region(us)"),
            "private"
        );
        assert_eq!(
            instance_label("wrld_a:123~canRequestInvite~region(eu)"),
            "invite+"
        );
        assert_eq!(instance_label("wrld_a:123~region(us)"), "public");
        assert_eq!(instance_label("wrld_a"), "");
    }

    #[test]
    fn locates_a_friend_in_a_resolved_world() {
        let mut located = friend("wrld_x:1~hidden(usr_9)");
        let worlds = HashMap::from([("wrld_x".to_string(), "Test World".to_string())]);
        locate(&mut located, &worlds);
        assert!(located.online);
        assert_eq!(located.world, "Test World");
        assert_eq!(located.instance, "friends+");
    }

    #[test]
    fn falls_back_to_the_world_id_when_the_name_is_unknown() {
        let mut located = friend("wrld_unknown:1");
        locate(&mut located, &HashMap::new());
        assert!(located.online);
        assert_eq!(located.world, "wrld_unknown");
        assert_eq!(located.instance, "public");
    }

    #[test]
    fn offline_and_private_locations_are_not_worlds() {
        let mut offline = friend("offline");
        locate(&mut offline, &HashMap::new());
        assert!(!offline.online);
        assert_eq!(offline.world, "");

        let mut private = friend("private");
        locate(&mut private, &HashMap::new());
        assert!(private.online);
        assert_eq!(private.world, "Private world");
        assert_eq!(private.instance, "private");
    }
}

const PAGE_ATTEMPTS: u32 = 4;
const FRIEND_CAP: usize = 2_500;
/// Each world sweep looks up at most this many ids.
const WORLD_SWEEP_BATCH: usize = 40;
/// Enough sweeps to cover 400 distinct worlds in one background run.
const WORLD_SWEEPS: usize = 10;

/// A sweep is worth repeating only while ids remain and it made progress;
/// otherwise the caller would loop forever on worlds VRChat refuses to name.
fn needs_another_sweep(pending_after: usize, pending_before: usize) -> bool {
    pending_after > 0 && pending_after < pending_before
}

/// Runs sweeps until every id is resolved. `resolve` performs one sweep and
/// returns whatever is still pending; the loop itself stays free of HTTP so it
/// can be driven by a stub.
async fn sweep_worlds<F, Fut>(pending: Vec<String>, mut resolve: F) -> Vec<String>
where
    F: FnMut(Vec<String>) -> Fut,
    Fut: std::future::Future<Output = Vec<String>>,
{
    let mut pending = pending;
    for _ in 0..WORLD_SWEEPS {
        if pending.is_empty() {
            break;
        }
        let before = pending.len();
        pending = resolve(pending).await;
        if !needs_another_sweep(pending.len(), before) {
            break;
        }
    }
    pending
}

enum PageOutcome {
    Ready(Vec<Value>),
    Throttled(Option<Duration>),
    Failed(PageFailure),
}

/// Why a page could not be read. The expired session is a variant, so callers
/// never have to match on the wording of an error message.
#[derive(Debug, PartialEq)]
enum PageFailure {
    Expired,
    Other(String),
}

impl PageFailure {
    fn to_message(self) -> String {
        match self {
            PageFailure::Expired => "Your session expired. Sign in again.".to_string(),
            PageFailure::Other(message) => message,
        }
    }
}

/// A response that retrying cannot fix. `None` means "try again later", which
/// covers throttling, server errors and dropped connections.
fn page_failure(status: u16) -> Option<PageFailure> {
    match status {
        401 => Some(PageFailure::Expired),
        429 | 500..=599 => None,
        status if status >= 400 => Some(PageFailure::Other(format!(
            "VRChat returned {status} while loading friends."
        ))),
        _ => None,
    }
}

/// Only a full page implies that another one exists.
fn needs_second_page(page_len: usize) -> bool {
    page_len == 100
}

/// Consumes the pages the quick load already fetched. Only a contiguous run from
/// offset 0 can be reused; anything else is left for the walk to fetch.
fn take_seed(
    seed: Vec<(usize, Vec<Value>)>,
    users: &mut Vec<Value>,
    seen: &mut HashSet<String>,
) -> (usize, bool, usize) {
    let mut offset = 0usize;
    let mut done = false;
    let mut reused = 0usize;
    for (seed_offset, page) in seed {
        if done || seed_offset != offset {
            break;
        }
        done = page.len() < 100;
        for user in page {
            push_unique(users, seen, user);
        }
        offset += 100;
        reused += 1;
    }
    (offset, done, reused)
}

/// Hitting the friend cap means the list was truncated, which is reported like
/// a dropped page so the roster is not called complete.
fn bucket_result(users: Vec<Value>, finished: bool, degraded: bool) -> (Vec<Value>, bool) {
    let capped = !finished && users.len() >= FRIEND_CAP;
    (users, degraded || capped)
}

/// Folds the optional second quick page. Returns whether it had to be dropped;
/// an expired session aborts instead.
fn fold_second_page(
    users: &mut Vec<Value>,
    second: Result<Vec<Value>, PageFailure>,
) -> Result<Option<Vec<Value>>, PageFailure> {
    match second {
        Ok(page) => {
            users.extend(page.iter().cloned());
            Ok(Some(page))
        }
        Err(PageFailure::Expired) => Err(PageFailure::Expired),
        Err(PageFailure::Other(_)) => Ok(None),
    }
}

/// Folds one round of page results into the roster. Returns whether the bucket
/// ended inside this round and whether any page was dropped. An expired session
/// is returned as an error so it aborts the load rather than degrading it.
fn fold_page_round(
    users: &mut Vec<Value>,
    results: Vec<Result<Vec<Value>, PageFailure>>,
) -> Result<(bool, bool), PageFailure> {
    let mut degraded = false;
    for result in results {
        match result {
            Ok(page) => {
                let short = page.len() < 100;
                users.extend(page);
                if short {
                    return Ok((true, degraded));
                }
            }
            // An expired session cannot be recovered by retrying.
            Err(PageFailure::Expired) => return Err(PageFailure::Expired),
            Err(PageFailure::Other(_)) => degraded = true,
        }
    }
    Ok((false, degraded))
}

/// 600 ms, 1.2 s, 2.4 s, 4.8 s; a `Retry-After` wins but never waits longer
/// than 10 s, so a stubborn endpoint cannot hang the roster.
fn backoff_delay(attempt: u32, retry_after: Option<Duration>) -> Duration {
    let sequential = Duration::from_millis(600u64 << attempt.saturating_sub(1).min(3));
    retry_after
        .unwrap_or(sequential)
        .min(Duration::from_secs(10))
}

fn retry_after_of(headers: &reqwest::header::HeaderMap) -> Option<Duration> {
    headers
        .get(reqwest::header::RETRY_AFTER)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.trim().parse::<u64>().ok())
        .map(Duration::from_secs)
        .filter(|delay| *delay <= Duration::from_secs(60))
}

fn push_unique(users: &mut Vec<Value>, seen: &mut HashSet<String>, user: Value) {
    let id = text(&user, "id");
    if id.is_empty() || !seen.insert(id) {
        return;
    }
    users.push(user);
}

/// The instance payload, mapped without any network access so it can be tested
/// against recorded fixtures.
pub(crate) fn parse_instance_body(location: &str, body: &Value) -> InstanceView {
    let world = body.get("world").cloned().unwrap_or(Value::Null);
    let world_id = {
        let from_body = text(&world, "id");
        if from_body.is_empty() {
            location
                .split('~')
                .next()
                .unwrap_or(location)
                .split(':')
                .next()
                .unwrap_or_default()
                .to_string()
        } else {
            from_body
        }
    };
    let thumbnail = {
        let candidate = text(&world, "thumbnailImageUrl");
        if candidate.is_empty() {
            text(&world, "imageUrl")
        } else {
            candidate
        }
    };
    let capacity = body
        .get("capacity")
        .and_then(Value::as_i64)
        .or_else(|| world.get("capacity").and_then(Value::as_i64))
        .unwrap_or(0);

    let mut players: Vec<Player> = body
        .get("players")
        .and_then(Value::as_array)
        .map(|rows| {
            rows.iter()
                .map(|row| Player {
                    id: text(row, "id"),
                    name: display_name(row),
                    status: text(row, "status"),
                    avatar: pick_avatar(row),
                    is_friend: row
                        .get("isFriend")
                        .and_then(Value::as_bool)
                        .unwrap_or(false),
                    trust: trust_label(row),
                })
                .collect()
        })
        .unwrap_or_default();
    players.sort_by(|left, right| {
        right
            .is_friend
            .cmp(&left.is_friend)
            .then_with(|| left.name.to_lowercase().cmp(&right.name.to_lowercase()))
    });

    InstanceView {
        in_instance: true,
        location: location.to_string(),
        world_id,
        world_name: text(&world, "name"),
        world_thumbnail: thumbnail,
        instance_type: instance_label(location),
        capacity,
        players,
        notice: String::new(),
    }
}

#[cfg(test)]
mod instance_fixture_tests {
    use super::*;

    const WITH_PLAYERS: &str = include_str!("../fixtures/instance-players.json");
    const WITHOUT_PLAYERS: &str = include_str!("../fixtures/instance-no-players.json");

    fn parse(raw: &str, location: &str) -> InstanceView {
        let body: Value = serde_json::from_str(raw).expect("fixture json");
        parse_instance_body(location, &body)
    }

    #[test]
    fn reads_world_capacity_and_players_from_the_payload() {
        let view = parse(WITH_PLAYERS, "wrld_test:12345~hidden(usr_owner)~region(us)");
        assert!(view.in_instance);
        assert_eq!(view.world_id, "wrld_test");
        assert_eq!(view.world_name, "Test World");
        assert_eq!(view.instance_type, "friends+");
        assert_eq!(view.capacity, 32);
        assert_eq!(view.players.len(), 3);
        assert_eq!(
            view.world_thumbnail,
            "https://api.vrchat.cloud/api/1/image/file_test/1/256.png"
        );
    }

    #[test]
    fn friends_come_first_then_alphabetical() {
        let view = parse(WITH_PLAYERS, "wrld_test:12345");
        let names: Vec<&str> = view.players.iter().map(|player| player.name.as_str()).collect();
        assert_eq!(names, vec!["Alpha", "Gamma", "Beta"]);
        assert!(view.players[0].is_friend);
        assert!(view.players[1].is_friend);
        assert!(!view.players[2].is_friend);
    }

    #[test]
    fn maps_status_trust_and_avatar_fallbacks() {
        let view = parse(WITH_PLAYERS, "wrld_test:12345");
        let alpha = &view.players[0];
        assert_eq!(alpha.name, "Alpha");
        assert_eq!(alpha.status, "join me");
        assert_eq!(alpha.trust, "Trusted");
        // Falls back from the thumbnail field to currentAvatarImageUrl.
        assert_eq!(
            alpha.avatar,
            "https://api.vrchat.cloud/api/1/image/file_alpha/1/1024.png"
        );
        assert_eq!(view.players[2].trust, "New");
        assert_eq!(view.players[1].trust, "User");
    }

    #[test]
    fn a_payload_without_players_still_describes_the_world() {
        let view = parse(WITHOUT_PLAYERS, "wrld_quiet:99~region(eu)");
        assert!(view.players.is_empty());
        assert_eq!(view.world_name, "Quiet World");
        assert_eq!(view.capacity, 20);
        assert_eq!(view.instance_type, "public");
        assert_eq!(
            view.world_thumbnail,
            "https://api.vrchat.cloud/api/1/image/file_quiet/1/256.png"
        );
    }

    #[test]
    fn an_empty_payload_falls_back_to_the_location() {
        let view = parse("{}", "wrld_missing:7~friends(usr_1)");
        assert_eq!(view.world_id, "wrld_missing");
        assert_eq!(view.world_name, "");
        assert_eq!(view.instance_type, "friends");
        assert_eq!(view.capacity, 0);
        assert!(view.players.is_empty());
    }
}

#[cfg(test)]
mod page_retry_tests {
    use super::*;

    #[test]
    fn throttling_and_server_errors_are_retried() {
        for status in [429, 500, 502, 503, 504, 599] {
            assert!(page_failure(status).is_none(), "status {status}");
        }
        assert!(page_failure(200).is_none());
    }

    #[test]
    fn only_401_counts_as_an_expired_session() {
        assert_eq!(page_failure(401), Some(PageFailure::Expired));

        for status in [400, 403, 404, 422] {
            match page_failure(status) {
                Some(PageFailure::Other(_)) => {}
                other => panic!("status {status} should be fatal, got {other:?}"),
            }
        }
    }

    #[test]
    fn the_expired_failure_carries_the_sign_in_message() {
        assert!(PageFailure::Expired
            .to_message()
            .contains("Sign in again"));
        assert_eq!(
            PageFailure::Other("VRChat returned 404 while loading friends.".to_string())
                .to_message(),
            "VRChat returned 404 while loading friends."
        );
    }

    #[test]
    fn a_second_quick_page_is_only_needed_after_a_full_page() {
        assert!(needs_second_page(100));
        assert!(!needs_second_page(99));
        assert!(!needs_second_page(1));
        assert!(!needs_second_page(0));
    }

    #[test]
    fn backoff_doubles_from_six_hundred_milliseconds() {
        assert_eq!(backoff_delay(1, None), Duration::from_millis(600));
        assert_eq!(backoff_delay(2, None), Duration::from_millis(1200));
        assert_eq!(backoff_delay(3, None), Duration::from_millis(2400));
        assert_eq!(backoff_delay(4, None), Duration::from_millis(4800));
        assert_eq!(backoff_delay(9, None), Duration::from_millis(4800));
    }

    #[test]
    fn a_retry_after_header_wins_but_is_capped() {
        assert_eq!(
            backoff_delay(1, Some(Duration::from_secs(3))),
            Duration::from_secs(3)
        );
        assert_eq!(
            backoff_delay(1, Some(Duration::from_secs(60))),
            Duration::from_secs(10)
        );
    }

    #[test]
    fn reads_retry_after_seconds_from_headers() {
        let mut headers = reqwest::header::HeaderMap::new();
        headers.insert(reqwest::header::RETRY_AFTER, "7".parse().expect("value"));
        assert_eq!(retry_after_of(&headers), Some(Duration::from_secs(7)));

        headers.insert(reqwest::header::RETRY_AFTER, "120".parse().expect("value"));
        assert_eq!(retry_after_of(&headers), None);

        headers.remove(reqwest::header::RETRY_AFTER);
        assert_eq!(retry_after_of(&headers), None);
    }
}

#[cfg(test)]
mod page_fold_tests {
    use super::*;

    fn page(len: usize) -> Vec<Value> {
        (0..len)
            .map(|index| serde_json::json!({ "id": format!("usr_{index}") }))
            .collect()
    }

    #[test]
    fn an_expired_page_aborts_the_bucket_instead_of_degrading_it() {
        let mut users = page(100);
        let results = vec![
            Ok(page(100)),
            Err(PageFailure::Expired),
            Ok(page(100)),
        ];

        assert_eq!(fold_page_round(&mut users, results), Err(PageFailure::Expired));
        // The pages already folded stay; the load does not continue silently.
        assert_eq!(users.len(), 200);
    }

    #[test]
    fn a_throttled_page_degrades_the_bucket_but_keeps_the_rest() {
        let mut users = Vec::new();
        let results = vec![
            Ok(page(100)),
            Err(PageFailure::Other("VRChat kept throttling friends page 200.".to_string())),
            Ok(page(100)),
        ];

        let (finished, degraded) = fold_page_round(&mut users, results).expect("no abort");
        assert!(!finished);
        assert!(degraded);
        assert_eq!(users.len(), 200);
    }

    #[test]
    fn a_short_page_ends_the_bucket() {
        let mut users = page(100);
        let (finished, degraded) = fold_page_round(&mut users, vec![Ok(page(7))]).expect("ok");
        assert!(finished);
        assert!(!degraded);
        assert_eq!(users.len(), 107);
    }

    #[test]
    fn an_expired_second_quick_page_aborts_the_quick_roster() {
        let mut users = page(100);
        assert_eq!(
            fold_second_page(&mut users, Err(PageFailure::Expired)),
            Err(PageFailure::Expired)
        );
        assert_eq!(users.len(), 100);
    }

    #[test]
    fn a_throttled_second_quick_page_only_degrades_it() {
        let mut users = page(100);
        let page = fold_second_page(
            &mut users,
            Err(PageFailure::Other("429 on page 100".to_string())),
        )
        .expect("degrade");
        assert!(page.is_none(), "nothing to hand to the walk");
        assert_eq!(users.len(), 100);
    }

    fn page_from(start: usize, len: usize) -> Vec<Value> {
        (start..start + len)
            .map(|index| serde_json::json!({ "id": format!("usr_{index}") }))
            .collect()
    }

    #[test]
    fn seeded_pages_are_reused_from_the_start_of_the_walk() {
        let mut users = Vec::new();
        let mut seen = HashSet::new();
        let seed = vec![(0, page_from(0, 100)), (100, page_from(100, 100))];

        let (offset, done, reused) = take_seed(seed, &mut users, &mut seen);

        assert_eq!(offset, 200, "the walk resumes after the pages already fetched");
        assert!(!done);
        assert_eq!(reused, 2);
        assert_eq!(users.len(), 200);
    }

    #[test]
    fn a_short_seeded_page_ends_the_bucket() {
        let mut users = Vec::new();
        let mut seen = HashSet::new();
        let seed = vec![(0, page_from(0, 100)), (100, page_from(100, 43))];

        let (offset, done, reused) = take_seed(seed, &mut users, &mut seen);

        assert_eq!(offset, 200);
        assert!(done, "a short page means there is nothing left to fetch");
        assert_eq!(reused, 2);
        assert_eq!(users.len(), 143);
    }

    #[test]
    fn a_gap_in_the_seed_stops_the_reuse() {
        let mut users = Vec::new();
        let mut seen = HashSet::new();
        let seed = vec![(0, page_from(0, 100)), (200, page_from(200, 100))];

        let (offset, done, reused) = take_seed(seed, &mut users, &mut seen);

        assert_eq!(offset, 100);
        assert!(!done);
        assert_eq!(reused, 1);
        assert_eq!(users.len(), 100);
    }
}

#[cfg(test)]
mod friend_cap_tests {
    use super::*;

    fn page(len: usize) -> Vec<Value> {
        (0..len)
            .map(|index| serde_json::json!({ "id": format!("usr_{index}") }))
            .collect()
    }

    #[test]
    fn hitting_the_friend_cap_marks_the_roster_degraded() {
        let (users, degraded) = bucket_result(page(FRIEND_CAP), false, false);
        assert_eq!(users.len(), FRIEND_CAP);
        assert!(degraded, "2,500 friends with more pages left is a truncation");

        let (users, degraded) = bucket_result(page(FRIEND_CAP), true, false);
        assert_eq!(users.len(), FRIEND_CAP);
        assert!(!degraded, "exactly 2,500 friends with no more pages is complete");

        let (_, degraded) = bucket_result(page(FRIEND_CAP - 1), false, false);
        assert!(!degraded, "below the cap is not a truncation");

        let (_, degraded) = bucket_result(page(12), false, true);
        assert!(degraded, "an existing degraded flag survives");
    }
}

#[cfg(test)]
mod world_sweep_tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[test]
    fn sweeps_repeat_only_while_they_make_progress() {
        assert!(needs_another_sweep(50, 90), "some ids resolved, more remain");
        assert!(!needs_another_sweep(0, 40), "everything is cached");
        assert!(!needs_another_sweep(40, 40), "no progress, stop instead of spinning");
    }

    fn block_on<F: std::future::Future>(future: F) -> F::Output {
        tokio::runtime::Builder::new_current_thread()
            .build()
            .expect("current-thread runtime")
            .block_on(future)
    }

    #[test]
    fn sweeps_repeat_until_a_stub_resolver_has_every_id() {
        use std::collections::HashSet;
        use std::sync::{Arc, Mutex};

        let all: Vec<String> = (0..90).map(|index| format!("wrld_{index}")).collect();
        let cached = Arc::new(Mutex::new(Vec::<String>::new()));
        let sweeps = Arc::new(AtomicUsize::new(0));
        let sweeps_for_stub = Arc::clone(&sweeps);
        let all_for_stub = Arc::new(all.clone());

        let remaining = block_on(sweep_worlds(all, move |pending: Vec<String>| {
            sweeps_for_stub.fetch_add(1, Ordering::SeqCst);
            let cached = Arc::clone(&cached);
            let all = Arc::clone(&all_for_stub);
            async move {
                // Stub resolver: names the first 40 pending ids per sweep, like
                // the real one, so 90 ids cannot fit in a single sweep.
                let mut guard = cached.lock().expect("stub cache");
                guard.extend(pending.iter().take(WORLD_SWEEP_BATCH).cloned());
                let done: HashSet<String> = guard.iter().cloned().collect();
                all.iter()
                    .filter(|id| !done.contains(*id))
                    .cloned()
                    .collect()
            }
        }));

        assert!(remaining.is_empty(), "every id should be resolved: {remaining:?}");
        assert_eq!(
            sweeps.load(Ordering::SeqCst),
            3,
            "90 ids at 40 per sweep need three sweeps"
        );
    }

    #[test]
    fn a_stub_that_stops_making_progress_ends_the_loop() {
        use std::sync::{Arc, Mutex};

        let sweeps = Arc::new(AtomicUsize::new(0));
        let sweeps_for_stub = Arc::clone(&sweeps);
        let stuck = Arc::new(Mutex::new(false));
        let stuck_for_stub = Arc::clone(&stuck);

        let pending = vec!["wrld_a".to_string(), "wrld_b".to_string()];
        let remaining = block_on(sweep_worlds(pending, move |pending: Vec<String>| {
            sweeps_for_stub.fetch_add(1, Ordering::SeqCst);
            let stuck = Arc::clone(&stuck_for_stub);
            async move {
                *stuck.lock().expect("stub state") = true;
                // Worlds VRChat refuses to name: cached as unresolved, unchanged.
                pending
            }
        }));

        assert_eq!(remaining.len(), 2);
        assert!(*stuck.lock().expect("stub state"));
        assert_eq!(
            sweeps.load(Ordering::SeqCst),
            1,
            "one fruitless sweep is enough to stop"
        );
    }

    fn friend_at(location: &str) -> Friend {
        Friend {
            id: "usr_1".to_string(),
            name: "A".to_string(),
            status: String::new(),
            status_description: String::new(),
            location: location.to_string(),
            world: String::new(),
            instance: String::new(),
            avatar: String::new(),
            online: false,
            trust: String::new(),
        }
    }

    #[test]
    fn a_snapshot_relabels_rows_named_after_it_was_built() {
        let api = Api::new();
        let mut items = vec![friend_at("wrld_late:1~hidden(usr_2)")];
        locate(&mut items[0], &HashMap::new());
        assert_eq!(items[0].world, "wrld_late");

        if let Ok(mut guard) = api.roster.lock() {
            *guard = Some(Roster {
                items,
                total: 1,
                online: 1,
                revision: 1,
                stale: false,
                live: false,
                worlds_pending: 1,
                complete: true,
                degraded: false,
                saved_at: 0,
                missing_worlds: vec!["wrld_late".to_string()],
            });
        }
        if let Ok(mut guard) = api.worlds.lock() {
            guard.insert("wrld_late".to_string(), "Late World".to_string());
        }

        let snapshot = api.roster_snapshot().expect("stored roster");
        assert_eq!(snapshot.items[0].world, "Late World");
        assert_eq!(snapshot.worlds_pending, 0);
    }

    #[test]
    fn missing_world_ids_lists_each_unnamed_online_world_once() {
        let api = Api::new();
        if let Ok(mut guard) = api.worlds.lock() {
            guard.insert("wrld_known".to_string(), "Known World".to_string());
        }

        let items = vec![
            friend_at("wrld_known:1"),
            friend_at("wrld_new:1~hidden(usr_1)"),
            friend_at("wrld_new:2"),
            friend_at("offline"),
        ];

        assert_eq!(api.missing_world_ids(&items), vec!["wrld_new".to_string()]);
    }

    #[test]
    fn uncached_worlds_returns_everything_still_missing() {
        let api = Api::new();
        let ids: Vec<String> = (0..90).map(|index| format!("wrld_{index}")).collect();
        if let Ok(mut guard) = api.worlds.lock() {
            for id in ids.iter().take(40) {
                guard.insert(id.clone(), "Cached World".to_string());
            }
        }

        let pending = api.uncached_worlds(&ids);
        assert_eq!(pending.len(), 50);
        assert_eq!(pending[0], "wrld_40");
    }
}

#[cfg(test)]
mod world_cache_tests {
    use super::*;

    fn temp_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!("kawaii-worlds-{}-{tag}.json", std::process::id()))
    }

    #[test]
    fn resolved_names_survive_a_round_trip_and_failures_do_not() {
        let path = temp_path("roundtrip");
        let names = HashMap::from([
            ("wrld_a".to_string(), "A World".to_string()),
            ("wrld_b".to_string(), String::new()),
        ]);
        let stamps = HashMap::from([("wrld_a".to_string(), now_secs())]);

        save_world_cache(&path, &names, &stamps, world_cache_cap(0));
        let cache = load_world_cache(&path);

        assert_eq!(cache.names.get("wrld_a").map(String::as_str), Some("A World"));
        assert!(
            !cache.names.contains_key("wrld_b"),
            "an empty name is a failed lookup and should be retried next launch"
        );
        assert!(cache.stamps.contains_key("wrld_a"), "the age is persisted");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_flat_file_from_an_older_build_still_loads() {
        let path = temp_path("legacy");
        std::fs::write(&path, r#"{"wrld_a":"A World","wrld_b":""}"#)
            .expect("write legacy cache");

        let cache = load_world_cache(&path);

        assert_eq!(cache.names.get("wrld_a").map(String::as_str), Some("A World"));
        assert!(!cache.names.contains_key("wrld_b"));
        assert!(
            cache.stamps.contains_key("wrld_a"),
            "legacy names get a fresh TTL instead of being thrown away"
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn names_past_their_ttl_are_dropped_so_renamed_worlds_refresh() {
        let path = temp_path("ttl");
        let old = now_secs() - WORLD_NAME_TTL_SECS - 60;
        let fresh = now_secs();
        std::fs::write(
            &path,
            format!(
                r#"{{"wrld_old":{{"name":"Old Name","at":{old}}},"wrld_new":{{"name":"New Name","at":{fresh}}}}}"#
            ),
        )
        .expect("write timed cache");

        let cache = load_world_cache(&path);

        assert!(!cache.names.contains_key("wrld_old"));
        assert_eq!(
            cache.names.get("wrld_new").map(String::as_str),
            Some("New Name")
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn the_cap_leaves_room_for_the_roster_within_a_hard_ceiling() {
        assert_eq!(world_cache_cap(0), WORLD_CACHE_MIN);
        assert_eq!(
            world_cache_cap(2_343),
            2_593,
            "a big friend list keeps a world per friend plus headroom"
        );
        assert_eq!(world_cache_cap(50_000), WORLD_CACHE_MAX);
    }

    #[test]
    fn saving_keeps_the_newest_entries_within_the_cap() {
        let path = temp_path("cap");
        let cap = 2_000;
        let now = now_secs();
        let mut names = HashMap::new();
        let mut stamps = HashMap::new();
        for index in 0..cap + 10 {
            let id = format!("wrld_{index:05}");
            names.insert(id.clone(), format!("World {index}"));
            stamps.insert(id, now - index as u64);
        }
        // An entry older than the TTL is dropped rather than saved.
        names.insert("wrld_expired".to_string(), "Expired".to_string());
        stamps.insert("wrld_expired".to_string(), now - WORLD_NAME_TTL_SECS - 1);

        save_world_cache(&path, &names, &stamps, cap);
        let cache = load_world_cache(&path);

        assert_eq!(cache.names.len(), cap);
        assert!(!cache.names.contains_key("wrld_expired"));
        assert!(cache.names.contains_key("wrld_00000"), "newest kept");
        assert!(
            !cache.names.contains_key(&format!("wrld_{cap:05}")),
            "oldest dropped"
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_missing_or_broken_cache_loads_empty() {
        let path = temp_path("missing");
        assert!(load_world_cache(&path).names.is_empty());

        std::fs::write(&path, "{ not json").expect("write a broken cache file");
        assert!(load_world_cache(&path).names.is_empty());
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn saving_leaves_no_temporary_file_behind() {
        let path = temp_path("atomic");
        let names = HashMap::from([("wrld_a".to_string(), "A World".to_string())]);

        save_world_cache(&path, &names, &HashMap::new(), 100);

        assert!(path.exists());
        assert!(!path.with_extension("tmp").exists());
        let _ = std::fs::remove_file(&path);
    }
}

#[cfg(test)]
mod roster_revision_tests {
    use super::*;

    #[test]
    fn publishing_a_roster_bumps_its_revision() {
        let api = Api::new();

        let first = api.publish(&[], false, false);
        let second = api.publish(&[], false, false);

        assert_eq!(first.revision, 1);
        assert_eq!(second.revision, 2, "pollers repaint only on a new revision");
        assert_eq!(
            api.roster_snapshot().expect("stored roster").revision,
            2
        );
    }

    #[test]
    fn page_requests_start_empty() {
        assert!(Api::new().page_requests().is_empty());
    }
}

#[cfg(test)]
mod presence_update_tests {
    use super::*;

    #[test]
    fn a_presence_event_updates_the_roster_row_in_place() {
        let api = Arc::new(Api::new());
        if let Ok(mut cache) = api.worlds.lock() {
            cache.insert("wrld_party".to_string(), "Party World".to_string());
        }
        let users = vec![serde_json::json!({
            "id": "usr_1",
            "displayName": "A",
            "status": "offline"
        })];
        api.publish(&users, true, false);
        assert!(!api.roster_snapshot().expect("roster").items[0].online);

        api.handle_realtime(
            r#"{"type":"friend-online","content":{"userId":"usr_1","location":"wrld_party:1~hidden(usr_2)","status":"active","statusDescription":"hi"}}"#,
        );

        let roster = api.roster_snapshot().expect("roster");
        let friend = &roster.items[0];
        assert!(friend.online, "the row goes online from the event");
        assert_eq!(friend.world, "Party World");
        assert_eq!(friend.instance, "friends+");
        assert_eq!(friend.status, "active");
        assert_eq!(friend.status_description, "hi");
        assert_eq!(roster.online, 1);
        assert_eq!(roster.revision, 1, "the list is patched, not republished");
    }

    #[test]
    fn a_presence_event_for_an_unloaded_friend_is_ignored() {
        let api = Arc::new(Api::new());
        api.publish(&[], true, false);

        api.handle_realtime(
            r#"{"type":"friend-location","content":{"userId":"usr_missing","location":"wrld_party:1"}}"#,
        );

        let roster = api.roster_snapshot().expect("roster");
        assert!(roster.items.is_empty());
        assert_eq!(roster.online, 0);
    }
}

#[cfg(test)]
mod snapshot_relabel_tests {
    use super::*;

    fn one_friend() -> Vec<Value> {
        vec![serde_json::json!({
            "id": "usr_1",
            "displayName": "A",
            "location": "wrld_x:1~hidden(usr_2)"
        })]
    }

    #[test]
    fn a_snapshot_that_picks_up_a_world_name_gets_a_new_revision() {
        let api = Api::new();
        api.publish(&one_friend(), true, false);

        let before = api.roster_snapshot().expect("roster");
        assert_eq!(before.items[0].world, "wrld_x");
        assert_eq!(before.worlds_pending, 1);

        if let Ok(mut cache) = api.worlds.lock() {
            cache.insert("wrld_x".to_string(), "Named World".to_string());
        }

        let after = api.roster_snapshot().expect("roster");
        assert_eq!(after.items[0].world, "Named World");
        assert_eq!(after.worlds_pending, 0);
        assert!(
            after.revision > before.revision,
            "a relabelled list must not be skipped as unchanged"
        );
    }

    #[test]
    fn an_unchanged_snapshot_keeps_its_revision() {
        let api = Api::new();
        api.publish(&one_friend(), true, false);

        let first = api.roster_snapshot().expect("roster");
        let second = api.roster_snapshot().expect("roster");

        assert_eq!(first.revision, second.revision);
        assert_eq!(second.items[0].world, "wrld_x");
    }

    #[test]
    fn world_resolution_can_start_without_a_runtime() {
        let api = Arc::new(Api::new());
        // Plain test thread: no tokio reactor, which used to panic here.
        api.resolve_worlds_in_background(vec!["wrld_a".to_string()]);
    }
}

#[cfg(test)]
mod roster_cache_tests {
    use super::*;

    fn temp_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!("kawaii-roster-{}-{tag}.json", std::process::id()))
    }

    fn friend(id: &str, location: &str) -> Friend {
        Friend {
            id: id.to_string(),
            name: id.to_string(),
            status: String::new(),
            status_description: String::new(),
            location: location.to_string(),
            world: String::new(),
            instance: String::new(),
            avatar: String::new(),
            online: location != "offline",
            trust: String::new(),
        }
    }

    fn stored(items: Vec<Friend>, complete: bool) -> Roster {
        Roster {
            total: items.len(),
            online: items.iter().filter(|friend| friend.online).count(),
            items,
            revision: 7,
            stale: false,
            live: true,
            worlds_pending: 1,
            complete,
            degraded: true,
            saved_at: 0,
            missing_worlds: vec!["wrld_x".to_string()],
        }
    }

    #[test]
    fn a_saved_roster_loads_back_as_a_complete_starting_picture() {
        let path = temp_path("roundtrip");
        save_roster_cache(
            &path,
            &stored(
                vec![friend("usr_a", "wrld_x:1"), friend("usr_b", "offline")],
                false,
            ),
        );

        let loaded = load_roster_cache(&path).expect("loaded roster");

        assert_eq!(loaded.items.len(), 2);
        assert_eq!(loaded.total, 2);
        assert_eq!(loaded.online, 1);
        assert_eq!(loaded.revision, 0, "the first live publish supersedes it");
        assert!(loaded.stale, "a saved roster is shown as stale until it is replaced");
        assert!(loaded.complete, "a saved roster is painted as the whole list");
        assert!(!loaded.live && !loaded.degraded);
        assert!(loaded.missing_worlds.is_empty());
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_missing_or_broken_roster_cache_loads_none() {
        let path = temp_path("missing");
        assert!(load_roster_cache(&path).is_none());

        std::fs::write(&path, "{ not a roster").expect("write broken file");
        assert!(load_roster_cache(&path).is_none());
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn the_first_live_publish_clears_the_saved_marker() {
        let api = Api::new();
        let mut saved = stored(vec![friend("usr_a", "offline")], true);
        saved.stale = true;
        if let Ok(mut guard) = api.roster.lock() {
            *guard = Some(saved);
        }

        let users = vec![serde_json::json!({ "id": "usr_a", "displayName": "A" })];
        let published = api.publish(&users, false, false);

        assert!(!published.stale, "a live page replaces the saved picture");
    }

    #[test]
    fn a_partial_publish_keeps_rows_from_the_roster_already_shown() {
        let api = Api::new();
        if let Ok(mut guard) = api.roster.lock() {
            *guard = Some(stored(
                vec![friend("usr_a", "offline"), friend("usr_b", "offline")],
                true,
            ));
        }

        let users = vec![serde_json::json!({ "id": "usr_a", "displayName": "A" })];
        let published = api.publish(&users, false, false);

        assert!(!published.complete);
        assert_eq!(
            published.items.len(),
            2,
            "the saved row stays while the first page loads"
        );
        assert_eq!(published.items[0].id, "usr_a");
    }

    #[test]
    fn a_complete_publish_drops_rows_the_walk_did_not_see() {
        let api = Api::new();
        if let Ok(mut guard) = api.roster.lock() {
            *guard = Some(stored(
                vec![friend("usr_a", "offline"), friend("usr_b", "offline")],
                true,
            ));
        }

        let users = vec![serde_json::json!({ "id": "usr_a", "displayName": "A" })];
        let published = api.publish(&users, true, false);

        assert!(published.complete);
        assert_eq!(published.items.len(), 1, "the finished walk is the truth");
    }
}

#[cfg(test)]
mod account_cache_tests {
    use super::*;


    #[test]
    fn each_account_gets_its_own_cache_files() {
        let first = cache_key("usr_aaa");
        let second = cache_key("usr_bbb");

        assert_ne!(roster_cache_path(&first), roster_cache_path(&second));
        assert_ne!(world_cache_path(&first), world_cache_path(&second));
        assert!(roster_cache_path(&first)
            .to_string_lossy()
            .contains("roster-snapshot-usr_aaa.json"));
    }

    #[test]
    fn odd_account_ids_cannot_escape_the_shell_folder() {
        let key = cache_key(r"../..\other?account");

        assert_eq!(key, "otheraccount");
        assert!(!roster_cache_path(&key).to_string_lossy().contains(".."));
    }

    #[test]
    fn switching_accounts_drops_the_previous_roster() {
        let api = Api::new();
        api.adopt_account("usr_aaa");
        api.publish(&[], true, false);
        assert!(api.roster_snapshot().is_some());

        api.adopt_account("usr_bbb");

        assert_eq!(api.current_account().as_deref(), Some("usr_bbb"));
        assert!(
            api.roster_snapshot().is_none(),
            "another account must not be shown the old list"
        );
    }

    #[test]
    fn the_same_account_again_keeps_the_roster_on_screen() {
        let api = Api::new();
        api.adopt_account("usr_aaa");
        api.publish(&[], true, false);
        let revision = api.roster_snapshot().expect("published roster").revision;

        api.adopt_account("usr_aaa");

        assert_eq!(api.roster_snapshot().expect("kept roster").revision, revision);
    }
}


#[cfg(test)]
mod roster_age_tests {
    use super::*;

    fn temp_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!("kawaii-roster-age-{}-{tag}.json", std::process::id()))
    }

    fn one_friend() -> Roster {
        let mut roster = Roster::default();
        roster.items.push(Friend {
            id: "usr_a".to_string(),
            name: "A".to_string(),
            ..Friend::default()
        });
        roster.total = 1;
        roster
    }

    #[test]
    fn a_saved_roster_remembers_when_it_was_written() {
        let path = temp_path("stamp");
        save_roster_cache(&path, &one_friend());

        let loaded = load_roster_cache(&path).expect("fresh snapshot loads");

        assert!(loaded.stale);
        assert!(
            now_secs().saturating_sub(loaded.saved_at) < 5,
            "the saved time is close to now"
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_snapshot_past_the_max_age_is_dropped_instead_of_painted() {
        let path = temp_path("old");
        let old = now_secs() - ROSTER_CACHE_MAX_AGE_SECS - 60;
        let json = serde_json::json!({
            "items": [{ "id": "usr_a", "name": "A" }],
            "total": 1,
            "online": 0,
            "complete": true,
            "saved_at": old,
        })
        .to_string();
        std::fs::write(&path, json).expect("write an old snapshot");

        assert!(load_roster_cache(&path).is_none());
        assert!(!path.exists(), "an expired snapshot is cleaned up");
    }

    #[test]
    fn a_snapshot_from_before_the_timestamp_is_dated_by_the_file() {
        let path = temp_path("unstamped");
        let json = serde_json::json!({
            "items": [{ "id": "usr_a", "name": "A" }],
            "total": 1,
            "online": 0,
            "complete": true,
        })
        .to_string();
        std::fs::write(&path, json).expect("write an unstamped snapshot");

        let loaded = load_roster_cache(&path).expect("a fresh file still loads");

        assert!(
            now_secs().saturating_sub(loaded.saved_at) < 5,
            "the file's own age stands in for the missing timestamp"
        );
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn the_legacy_shared_snapshot_is_retired() {
        let dir = std::env::temp_dir().join(format!("kawaii-retire-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("temp dir");
        let legacy = dir.join("roster-snapshot.json");
        let scoped = dir.join("roster-snapshot-usr_a.json");
        std::fs::write(&legacy, "{}").expect("legacy file");
        std::fs::write(&scoped, "{}").expect("scoped file");

        retire_legacy_roster_cache(&dir);

        assert!(!legacy.exists(), "the unused shared file is gone");
        assert!(scoped.exists(), "the per-account file stays");
        let _ = std::fs::remove_dir_all(&dir);
    }
}

#[cfg(test)]
mod friend_action_tests {
    use super::*;

    #[test]
    fn an_invite_needs_an_instance_we_are_actually_in() {
        assert_eq!(
            invite_instance_from("wrld_abc:123~region(us)").as_deref(),
            Ok("wrld_abc:123~region(us)")
        );
        for location in ["offline", "private", "traveling", ""] {
            assert!(
                invite_instance_from(location).is_err(),
                "{location:?} is not an instance"
            );
        }
    }

    #[test]
    fn friend_favorites_keep_the_row_id_needed_to_remove_them() {
        let body = r#"[
            {"id":"fvrt_a","favoriteId":"usr_a","type":"friend","tags":["group_0"]},
            {"id":"fvrt_w","favoriteId":"wrld_a","type":"world","tags":["worlds1"]},
            {"id":"fvrt_b","favoriteId":"usr_b","type":"friend","tags":[]},
            {"id":"fvrt_c","type":"friend"}
        ]"#;

        let rows = friend_favorites_from(body);

        assert_eq!(rows.len(), 2);
        assert_eq!(rows.get("usr_a").map(String::as_str), Some("fvrt_a"));
        assert_eq!(rows.get("usr_b").map(String::as_str), Some("fvrt_b"));
    }

    #[test]
    fn a_broken_or_empty_favorites_body_yields_nothing() {
        assert!(friend_favorites_from("not json").is_empty());
        assert!(friend_favorites_from("[]").is_empty());
    }

    #[test]
    fn a_vrchat_error_body_becomes_the_message() {
        let body =
            r#"{"error":{"message":"You need to be friends with that user first.","status_code":403}}"#;

        assert_eq!(
            api_message(reqwest::StatusCode::FORBIDDEN, body),
            "You need to be friends with that user first."
        );
        assert_eq!(
            api_message(reqwest::StatusCode::UNAUTHORIZED, "{}"),
            "Your session expired. Sign in again."
        );
        assert_eq!(
            api_message(reqwest::StatusCode::INTERNAL_SERVER_ERROR, ""),
            "VRChat returned 500 for that action."
        );
    }
}

#[cfg(test)]
mod notice_location_tests {
    use super::*;

    #[test]
    fn an_invite_notification_names_the_instance_it_invites_to() {
        let modern = serde_json::json!({
            "type": "invite",
            "details": { "worldId": "wrld_a", "instanceId": "123~region(us)" }
        });
        assert_eq!(notice_location(&modern), "wrld_a:123~region(us)");

        let location = serde_json::json!({
            "type": "invite",
            "details": { "location": "wrld_b:45~friends(usr_x)" }
        });
        assert_eq!(notice_location(&location), "wrld_b:45~friends(usr_x)");
    }

    #[test]
    fn details_written_as_a_json_string_still_name_the_instance() {
        let row = serde_json::json!({
            "type": "invite",
            "details": "{\"worldId\":\"wrld_c\",\"instanceId\":\"77\"}"
        });
        assert_eq!(notice_location(&row), "wrld_c:77");
    }

    #[test]
    fn notifications_without_a_usable_instance_are_left_alone() {
        assert_eq!(notice_location(&serde_json::json!({ "type": "friendRequest" })), "");
        assert_eq!(
            notice_location(&serde_json::json!({ "details": { "worldId": "wrld_a" } })),
            ""
        );
    }
}

#[cfg(test)]
mod notification_record_tests {
    use super::*;

    #[test]
    fn a_notification_is_read_from_its_own_record() {
        let row = serde_json::json!({
            "id": "not_1",
            "type": "invite",
            "senderUserId": "usr_a",
            "senderDisplayName": "A",
            "details": { "worldId": "wrld_a", "instanceId": "7" },
            "created_at": "2026-01-01T00:00:00Z",
            "seen": false
        });

        let notice = notice_from(&row);

        assert_eq!(notice.id, "not_1");
        assert_eq!(notice.kind, "invite");
        assert_eq!(notice.sender, "A");
        assert_eq!(notice.sender_id, "usr_a");
        assert_eq!(notice.location, "wrld_a:7");
    }

    #[test]
    fn only_actionable_notification_kinds_count_as_waiting() {
        for kind in ["friendRequest", "invite", "requestInvite"] {
            assert!(actionable_kind(kind), "{kind} is waiting on a reply");
        }
        for kind in ["friendRequestResponse", "inviteResponse", "message", "boop", ""] {
            assert!(!actionable_kind(kind), "{kind} needs no reply");
        }
    }

    #[test]
    fn only_vrchat_ids_go_into_request_paths() {
        assert_eq!(path_id("frq_abc-123").as_deref(), Ok("frq_abc-123"));
        assert_eq!(path_id("  usr_a  ").as_deref(), Ok("usr_a"));

        for bad in ["", "../../auth/user", "frq/x", "frq abc", "frq?x=1"] {
            assert!(path_id(bad).is_err(), "{bad:?} must not be a path");
        }
        let long = "a".repeat(129);
        assert!(path_id(&long).is_err(), "overlong ids are rejected too");
    }
}

#[cfg(test)]
mod notice_paging_tests {
    use super::*;

    #[test]
    fn the_page_size_decides_whether_more_may_follow() {
        assert!(page_has_more(NOTICE_PAGE));
        assert!(!page_has_more(NOTICE_PAGE - 1));
        assert!(!page_has_more(0));
    }

    #[test]
    fn only_actionable_requests_are_counted_by_id() {
        let rows = vec![
            serde_json::json!({ "id": "frq_a", "type": "friendRequest" }),
            serde_json::json!({ "id": "not_b", "type": "invite" }),
            serde_json::json!({ "id": "not_c", "type": "requestInvite" }),
            serde_json::json!({ "id": "not_d", "type": "message" }),
            serde_json::json!({ "id": "not_e", "type": "friendRequestResponse" }),
        ];

        let ids = actionable_ids(&rows);

        assert_eq!(ids.len(), 3);
        assert!(ids.contains("frq_a"));
        assert!(ids.contains("not_b"));
        assert!(ids.contains("not_c"));
        assert!(!ids.contains("not_d"));
    }
}

#[cfg(test)]
mod notice_count_tests {
    use super::*;

    fn page(ids: &[&str]) -> HashSet<String> {
        ids.iter().map(|id| id.to_string()).collect()
    }

    #[test]
    fn new_requests_on_the_first_page_are_added_once() {
        let mut cache = NoticeCount::default();

        merge_notice_page(&mut cache, page(&["frq_a", "frq_b"]));
        merge_notice_page(&mut cache, page(&["frq_a", "frq_b", "frq_c"]));
        merge_notice_page(&mut cache, page(&["frq_a"]));

        assert_eq!(cache.ids.len(), 3, "a, b and c each count once");
    }

    #[test]
    fn a_request_leaving_the_first_page_is_checked_before_it_is_dropped() {
        let mut cache = NoticeCount::default();
        merge_notice_page(&mut cache, page(&["frq_a", "frq_b"]));

        merge_notice_page(&mut cache, page(&["frq_b"]));

        assert_eq!(cache.ids.len(), 2, "still counted until the check answers");
        assert_eq!(cache.checks.len(), 1);
        assert_eq!(cache.checks.front().map(String::as_str), Some("frq_a"));

        settle_notice_check(&mut cache, "frq_a", false);
        assert_eq!(cache.ids.len(), 1, "answered elsewhere, so it stops counting");

        merge_notice_page(&mut cache, page(&["frq_b"]));
        assert_eq!(cache.ids.len(), 1, "settling twice cannot subtract twice");
    }

    #[test]
    fn a_request_pushed_down_the_list_keeps_counting() {
        let mut cache = NoticeCount::default();
        merge_notice_page(&mut cache, page(&["frq_a", "frq_b"]));
        merge_notice_page(&mut cache, page(&["not_new"]));

        assert_eq!(cache.checks.len(), 2, "both older requests left the page");

        settle_notice_check(&mut cache, "frq_a", true);
        settle_notice_check(&mut cache, "frq_b", true);

        assert_eq!(
            cache.ids.len(),
            3,
            "the new request counts, and the two pushed down still exist"
        );
        assert!(cache.checks.is_empty());
    }

    #[test]
    fn an_id_that_leaves_and_comes_back_is_not_queued_twice() {
        let mut cache = NoticeCount::default();
        merge_notice_page(&mut cache, page(&["frq_a", "frq_b"]));
        merge_notice_page(&mut cache, page(&["frq_b"]));
        merge_notice_page(&mut cache, page(&["frq_a", "frq_b"]));

        assert_eq!(cache.checks.len(), 1, "keeping frq_a queued once");
        assert_eq!(cache.ids.len(), 2);
    }
}
