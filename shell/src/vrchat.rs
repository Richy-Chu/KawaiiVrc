use crate::session;
use futures::future::join_all;
use futures::sink::SinkExt;
use futures::stream::StreamExt;
use serde::Serialize;
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::Message;

const BASE: &str = "https://api.vrchat.cloud/api/1/";
const PIPELINE: &str = "wss://pipeline.vrchat.cloud";
const USER_AGENT: &str = "KawaiiVrcShell/0.2.0 (unofficial Windows companion)";

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
    pub created_at: String,
    pub seen: bool,
}

#[derive(Serialize, Clone)]
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

#[derive(Serialize, Clone)]
pub struct Roster {
    pub items: Vec<Friend>,
    pub total: usize,
    pub online: usize,
    pub live: bool,
    /// In-world friends whose world name is not cached yet; the UI keeps
    /// reading the cache while the background lookup fills it in.
    pub worlds_pending: usize,
    /// False while only the first page has been loaded.
    pub complete: bool,
    /// True when some pages were dropped after retries, so the list may be short.
    pub degraded: bool,
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
    auth: Mutex<Option<String>>,
    user: Mutex<Option<UserInfo>>,
    worlds: Mutex<HashMap<String, String>>,
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
}

impl Api {
    pub fn new() -> Self {
        let http = reqwest::Client::builder()
            .user_agent(USER_AGENT)
            .timeout(Duration::from_secs(25))
            .build()
            .expect("http client");
        Self {
            http,
            auth: Mutex::new(None),
            user: Mutex::new(None),
            worlds: Mutex::new(HashMap::new()),
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
        for bucket in ["false", "true"] {
            let (page_users, page_degraded) = self
                .fetch_bucket(&cookie, bucket)
                .await
                .map_err(PageFailure::to_message)?;
            degraded |= page_degraded;
            for user in page_users {
                push_unique(&mut users, &mut seen, user);
            }
            if users.len() >= FRIEND_CAP {
                break;
            }
        }

        let roster = self.build_roster(&users, true, degraded);
        if let Ok(mut guard) = self.roster.lock() {
            *guard = Some(roster.clone());
        }
        Ok(roster)
    }

    /// The first online page, plus a second one when the first page was full,
    /// so the UI paints in well under a second and accounts with more than 100
    /// online friends are not truncated.
    pub async fn friends_quick(&self) -> Result<Roster, String> {
        let cookie = self.cookie().ok_or_else(|| "Not signed in.".to_string())?;
        let mut users = self
            .fetch_page(&cookie, "false", 0)
            .await
            .map_err(PageFailure::to_message)?;
        let mut degraded = false;
        if needs_second_page(users.len()) {
            let second = self.fetch_page(&cookie, "false", 100).await;
            degraded = fold_second_page(&mut users, second).map_err(PageFailure::to_message)?;
        }

        Ok(self.build_roster(&users, false, degraded))
    }

    pub fn roster_snapshot(&self) -> Option<Roster> {
        let mut roster = self.roster.lock().ok().and_then(|guard| guard.clone())?;
        // Names keep landing after the roster was built, so relabel the rows
        // before handing them out: a poll must never put a raw id back on a row
        // whose world is now cached.
        let worlds = self.world_names();
        for friend in &mut roster.items {
            locate(friend, &worlds);
        }
        roster.worlds_pending = roster
            .missing_worlds
            .iter()
            .filter(|id| !worlds.contains_key(*id))
            .count();
        Some(roster)
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

        let mut seen_worlds: HashSet<String> = HashSet::new();
        let missing_worlds: Vec<String> = items
            .iter()
            .filter_map(|friend| world_id_of(&friend.location))
            .filter(|id| !worlds.contains_key(id))
            .filter(|id| seen_worlds.insert(id.clone()))
            .collect();

        Roster {
            total: items.len(),
            online: items.iter().filter(|friend| friend.online).count(),
            live: self.live.load(Ordering::SeqCst),
            worlds_pending: missing_worlds.len(),
            missing_worlds,
            complete,
            degraded,
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

    /// Walks every page of one bucket six requests at a time. A page that stays
    /// throttled is reported instead of aborting the whole roster.
    async fn fetch_bucket(
        &self,
        cookie: &str,
        bucket: &str,
    ) -> Result<(Vec<Value>, bool), PageFailure> {
        let first = self.fetch_page(cookie, bucket, 0).await?;
        let mut users = first;
        let mut finished = users.len() < 100;
        let mut degraded = false;
        let mut offset = 100usize;

        while !finished && users.len() < FRIEND_CAP && offset < 3_000 {
            let offsets: Vec<usize> = (0..6).map(|index| offset + index * 100).collect();
            let results = join_all(
                offsets
                    .iter()
                    .map(|offset| self.fetch_page(cookie, bucket, *offset)),
            )
            .await;
            let (round_finished, round_degraded) = fold_page_round(&mut users, results)?;
            finished = round_finished;
            degraded |= round_degraded;
            offset += 600;
        }

        Ok(bucket_result(users, finished, degraded))
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
        tokio::spawn(async move {
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
        });
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

        if let Ok(mut guard) = self.worlds.lock() {
            *guard = merged.clone();
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

    fn handle_realtime(&self, raw: &str) {
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
            }
            "friend-delete" => {
                if let Ok(mut presence) = self.presence.lock() {
                    presence.remove(&event.user_id);
                }
            }
            _ => {}
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
        Ok(rows
            .iter()
            .map(|row| {
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
                    created_at: created,
                    seen: row.get("seen").and_then(Value::as_bool).unwrap_or(false),
                }
            })
            .collect())
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
) -> Result<bool, PageFailure> {
    match second {
        Ok(page) => {
            users.extend(page);
            Ok(false)
        }
        Err(PageFailure::Expired) => Err(PageFailure::Expired),
        Err(PageFailure::Other(_)) => Ok(true),
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
        let degraded = fold_second_page(
            &mut users,
            Err(PageFailure::Other("429 on page 100".to_string())),
        )
        .expect("degrade");
        assert!(degraded);
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
                live: false,
                worlds_pending: 1,
                complete: true,
                degraded: false,
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
