const invoke = window.__TAURI__.core.invoke;

const state = {
  signedIn: false,
  user: null,
  friends: [],
  feed: null,
  instances: [],
  instance: null,
  media: [],
  mediaOffset: 0,
  mediaLoading: false,
  notices: [],
  settings: { refresh_seconds: 60, show_offline: true, animate_backdrop: true },
  query: "",
  tab: "friends",
  busy: false,
  timer: 0,
  rosterRevision: 0,
  rosterComplete: false,
  rosterStale: false,
  rosterSavedAt: 0
};

const el = (id) => document.getElementById(id);
const activityList = el("activity-list");
const instanceList = el("instance-list");
const instanceBody = el("instance-body");
const roster = el("roster");
const mediaList = el("media-list");
const noticeList = el("notification-list");
const status = el("status");
const search = el("search");
const chips = el("chips");
const accountLine = el("account");
const userbox = el("userbox");
const signinForm = el("signin");
const twoFactorForm = el("twofactor");

const escape = (value) =>
  String(value ?? "").replace(/[&<>"']/g, (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;"
  })[character]);

const bytes = (value) => {
  if (!value) return "0 MB";
  const mb = value / (1024 * 1024);
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb.toFixed(mb >= 10 ? 0 : 1)} MB`;
};

const clock = (value) => {
  if (!value) return "";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return date.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  });
};

const remoteImage = (url) =>
  url ? `http://kawaii.localhost/avatar?u=${encodeURIComponent(url)}` : "";
const localImage = (path) =>
  path ? `http://kawaii.localhost/avatar?f=${encodeURIComponent(path)}` : "";

const dotClass = (status) => `dot-${String(status || "offline").replace(/\s+/g, "-")}`;

const worldIdOf = (location) => {
  if (!location || !location.startsWith("wrld_")) return "";
  return location.split("~")[0].split(":")[0];
};

function setBusy(busy, text) {
  state.busy = busy;
  el("signin-button").disabled = busy;
  el("verify-button").disabled = busy;
  el("refresh").disabled = busy;
  if (text) status.textContent = text;
}

function showAuth(message = "") {
  state.signedIn = false;
  state.friends = [];
  userbox.hidden = true;
  roster.innerHTML = "";
  signinForm.hidden = false;
  twoFactorForm.hidden = true;
  el("auth-error").textContent = message;
}

function showUser(user) {
  state.signedIn = true;
  state.user = user;
  signinForm.hidden = true;
  twoFactorForm.hidden = true;
  userbox.hidden = false;
  el("me-name").textContent = user.display_name;
  el("me-status").textContent = [user.status, user.trust].filter(Boolean).join(" · ");
  if (user.avatar) {
    el("me").src = remoteImage(user.avatar);
  } else {
    el("me").removeAttribute("src");
  }
}

function renderChips(summary) {
  chips.innerHTML = [
    `<span class="chip"><b>${summary.accounts}</b> accounts</span>`,
    `<span class="chip">activity <b>${bytes(summary.activity_bytes)}</b></span>`,
    `<span class="chip">history <b>${bytes(summary.history_bytes)}</b></span>`,
    `<span class="chip">media index <b>${bytes(summary.media_bytes)}</b></span>`,
    `<span class="chip">shell events <b>${summary.logged_events}</b></span>`
  ].join("");
}

// ------------------------------------------------------------------ friends

function friendRow(friend) {
  const where = friend.online
    ? [friend.world, friend.instance].filter(Boolean).join(" · ")
    : "Offline";
  return `
    <article class="row friend">
      <span class="status-dot ${dotClass(friend.status)}" title="${escape(friend.status)}"></span>
      ${friend.avatar ? `<img class="avatar" loading="lazy" src="${remoteImage(friend.avatar)}" alt="" />` : `<span class="avatar"></span>`}
      <div class="body">
        <div class="title">
          <strong>${escape(friend.name)}</strong>
          ${friend.trust ? `<span class="chip trust">${escape(friend.trust)}</span>` : ""}
          ${friend.status_description ? `<span class="muted">${escape(friend.status_description)}</span>` : ""}
        </div>
        <div class="sub">${escape(where)}</div>
      </div>
      <span class="kind">${escape(friend.status)}</span>
    </article>`;
}

// A row from the saved roster: name and trust only. Its presence and world are
// from last session, so showing them would read as current.
function savedFriendRow(friend) {
  return `
    <article class="row friend">
      ${friend.avatar ? `<img class="avatar" loading="lazy" src="${remoteImage(friend.avatar)}" alt="" />` : `<span class="avatar"></span>`}
      <div class="body">
        <div class="title">
          <strong>${escape(friend.name)}</strong>
          ${friend.trust ? `<span class="chip trust">${escape(friend.trust)}</span>` : ""}
        </div>
      </div>
    </article>`;
}

function renderRoster() {
  if (!state.signedIn) return;
  const query = state.query.trim().toLowerCase();
  const matches = state.friends.filter(
    (friend) =>
      !query ||
      `${friend.name} ${friend.world} ${friend.status} ${friend.status_description}`
        .toLowerCase()
        .includes(query)
  );

  if (matches.length === 0) {
    roster.innerHTML = `<div class="empty">${state.friends.length === 0 ? "No friends on this account yet." : "No friends match that filter."}</div>`;
    return;
  }

  const byName = (a, b) => a.name.localeCompare(b.name);

  // Presence in the saved roster is from last session, so it is a plain list
  // rather than In game / Online groups that would look current.
  if (state.rosterStale) {
    const rows = matches.slice().sort(byName);
    roster.innerHTML = `
      <div class="group-head">Saved roster (${rows.length})</div>
      ${rows.map(savedFriendRow).join("")}`;
    return;
  }

  const inGame = matches.filter((friend) => friend.online && worldIdOf(friend.location));
  const online = matches.filter((friend) => friend.online && !inGame.includes(friend));
  const offline = matches.filter((friend) => !friend.online);

  const offlineShown = query ? offline.slice(0, 600) : offline.slice(0, 300);
  const groups = [
    ["In game", inGame.sort(byName)],
    ["Online", online.sort(byName)],
    [`Offline (${offline.length})`, state.settings.show_offline ? offlineShown.sort(byName) : []]
  ];

  roster.innerHTML = groups
    .filter(([, rows]) => rows.length > 0)
    .map(
      ([head, rows]) => `
        <div class="group-head">${escape(head)}</div>
        ${rows.map(friendRow).join("")}`
    )
    .join("");
}

// Realtime presence: patch the affected row instead of waiting for a refresh.
let presenceRenderTimer = 0;
function scheduleRosterRender() {
  if (presenceRenderTimer) return;
  presenceRenderTimer = setTimeout(() => {
    presenceRenderTimer = 0;
    renderRoster();
  }, 300);
}

function applyPresence(update) {
  if (!update || !update.friend) return;
  const friend = state.friends.find((row) => row.id === update.friend.id);
  if (!friend) return;
  Object.assign(friend, update.friend);
  accountLine.textContent = accountLabel(update.total, update.online);
  const worldId = worldIdOf(friend.location);
  if (worldId && friend.world === worldId) fillWorldNames();
  scheduleRosterRender();
}

const listenPresence = window.__TAURI__?.event?.listen;
if (listenPresence) {
  listenPresence("friend-presence", (event) => applyPresence(event.payload));
}

// World names arrive after the roster is on screen; patch them in as they land.
async function fillWorldNames(attempt = 0) {
  if (attempt > 15) return;
  try {
    const names = await invoke("friend_worlds");
    let changed = false;
    for (const friend of state.friends) {
      const id = worldIdOf(friend.location);
      const name = id ? names[id] : "";
      if (name && friend.world !== name) {
        friend.world = name;
        changed = true;
      }
    }
    if (changed) renderRoster();

    const pending = state.friends.some((friend) => {
      const id = worldIdOf(friend.location);
      return id && !names[id];
    });
    if (pending) setTimeout(() => fillWorldNames(attempt + 1), 1500);
  } catch (error) {
    // Leave the world ids visible rather than blanking the rows.
  }
}

// How old the saved roster is, so it never reads as this session's list.
function savedAge(savedAt) {
  if (!savedAt) return "saved";
  const minutes = Math.round((Date.now() / 1000 - savedAt) / 60);
  if (minutes < 2) return "saved just now";
  if (minutes < 90) return `saved ${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 36) return `saved ${hours} h ago`;
  return `saved ${Math.round(hours / 24)} days ago`;
}

// A saved roster's counts are from last session, so it shows only the total
// until a live roster lands.
function accountLabel(total, online) {
  const friends = `${total.toLocaleString()} friends`;
  const who = state.user?.display_name ?? "";
  if (state.rosterStale) return `${friends} (${savedAge(state.rosterSavedAt)}) · ${who}`;
  const suffix = state.rosterComplete ? "" : " (first page)";
  return `${friends}${suffix} · ${online} online · ${who}`;
}

function applyRoster(roster) {
  state.friends = roster.items;
  state.rosterRevision = roster.revision ?? state.rosterRevision;
  state.rosterComplete = Boolean(roster.complete);
  state.rosterStale = Boolean(roster.stale);
  state.rosterSavedAt = roster.saved_at || 0;
  accountLine.textContent = accountLabel(roster.total, roster.online);
  renderRoster();
  if (roster.worlds_pending > 0) fillWorldNames();
  status.textContent = roster.stale
    ? `Showing the roster ${savedAge(roster.saved_at || 0)}; refreshing…`
    : roster.degraded
      ? "Some friend pages were throttled; the next refresh picks them up."
      : roster.live
        ? "Live presence from VRChat's realtime pipeline."
        : "Roster loaded; presence is reconnecting.";
}

// Snapshots grow as the walk fetches pages, so paint each one as it lands
// instead of holding the list back until the last page arrives.
async function waitForFullRoster(attempts = 30) {
  let latest = null;
  for (let index = 0; index < attempts; index += 1) {
    await new Promise((resolve) => setTimeout(resolve, 400));
    const snapshot = await invoke("friends_snapshot");
    if (!snapshot) continue;
    // Only repaint when the backend published a new roster.
    if (snapshot.revision !== state.rosterRevision) {
      latest = snapshot;
      applyRoster(snapshot);
    }
    if (snapshot.complete) return snapshot;
  }
  return latest;
}

async function loadFriends() {
  if (!state.signedIn || state.busy) return;
  setBusy(true, "Loading friends from VRChat…");
  // Paint the roster remembered from last launch before touching the network.
  try {
    const cached = await invoke("friends_snapshot");
    if (cached && cached.items.length > 0) applyRoster(cached);
  } catch (error) {
    // The fresh load below is the fallback.
  }
  try {
    // First page paints immediately; the rest of the roster arrives behind it.
    const quick = await invoke("friends_quick");
    applyRoster(quick);
    setBusy(false);

    const full = await waitForFullRoster();
    if (!full || !full.complete) {
      status.textContent = "Showing the first page; the full roster is still loading.";
    }
  } catch (error) {
    const message = String(error);
    if (/sign in again|expired/i.test(message)) {
      showAuth(message);
      status.textContent = "Session expired — sign in again.";
    } else {
      status.textContent = message;
    }
  } finally {
    setBusy(false);
  }
}

// ----------------------------------------------------------------- instance

function renderInstance() {
  const view = state.instance;
  if (!view) {
    instanceBody.innerHTML = `<div class="empty">Sign in to see the instance you are in.</div>`;
    return;
  }
  if (!view.in_instance) {
    instanceBody.innerHTML = `<div class="empty">You are not in a world right now (${escape(view.location || "offline")}).</div>`;
    return;
  }

  const cover = view.world_thumbnail
    ? `<img class="cover" loading="lazy" src="${remoteImage(view.world_thumbnail)}" alt="" />`
    : "";
  const players = view.players.length
    ? view.players
        .map(
          (player) => `
      <article class="row">
        ${player.avatar ? `<img class="avatar" loading="lazy" src="${remoteImage(player.avatar)}" alt="" />` : `<span class="avatar"></span>`}
        <div class="body">
          <div class="title">
            <strong>${escape(player.name)}</strong>
            ${player.is_friend ? `<span class="chip trust">friend</span>` : ""}
            ${player.trust ? `<span class="chip trust">${escape(player.trust)}</span>` : ""}
          </div>
          <div class="sub">${escape(player.status)}</div>
        </div>
      </article>`
        )
        .join("")
    : `<div class="empty">${escape(view.notice || "VRChat returned no players for this instance.")}</div>`;

  instanceBody.innerHTML = `
    <article class="world-card">
      ${cover}
      <div class="body">
        <h2>${escape(view.world_name || view.world_id)}</h2>
        <p class="sub">${escape(view.instance_type)} · ${view.players.length}${
    view.capacity > 0 ? ` / ${view.capacity}` : ""
  } players</p>
        ${view.notice ? `<p class="error">${escape(view.notice)}</p>` : ""}
      </div>
    </article>
    <div class="group-head">Players</div>
    ${players}`;
}

async function loadInstance() {
  if (!state.signedIn) {
    renderInstance();
    return;
  }
  try {
    state.instance = await invoke("instance");
    renderInstance();
  } catch (error) {
    instanceBody.innerHTML = `<div class="empty">${escape(error)}</div>`;
  }
}

// ------------------------------------------------------------------ favorites

function renderFavorites(view) {
  if (!view) return;
  const entries = [...view.avatars, ...view.worlds];
  const resolved = entries.filter((entry) => entry.cached).length;
  el("favorite-status").textContent =
    `${entries.length} favorites · ${resolved} with details` +
    (view.pending ? ` · ${view.pending} left to fetch` : "");

  el("favorite-grid").innerHTML = entries.length
    ? entries
        .map(
          (entry) => `
      <figure class="tile">
        ${entry.thumbnail ? `<img loading="lazy" src="${remoteImage(entry.thumbnail)}" alt="" />` : `<img alt="" />`}
        <figcaption title="${escape(entry.id)}">${escape(entry.missing ? `${entry.id} · unavailable` : entry.name || entry.id)}<span class="kind">${escape(entry.kind)}</span></figcaption>
      </figure>`
        )
        .join("")
    : `<div class="empty">No favorites saved in the other app yet.</div>`;
}

async function loadFavorites() {
  if (!state.signedIn) {
    el("favorite-status").textContent = "Sign in to resolve favorite details.";
    return;
  }
  try {
    let view = await invoke("favorites");
    renderFavorites(view);
    let guard = 0;
    // Two ids per pass, just over a second apart: VRChat allows about one
    // lookup per second, and the pane fills in as they arrive.
    while (view.pending > 0 && guard < 40) {
      guard += 1;
      await new Promise((resolve) => setTimeout(resolve, 2200));
      view = await invoke("refresh_favorites", { budget: 2 });
      renderFavorites(view);
    }
  } catch (error) {
    el("favorite-status").textContent = String(error);
  }
}

// ------------------------------------------------------------------- activity

function renderActivity() {
  if (!state.feed) return;
  const query = state.query.trim().toLowerCase();
  const items = state.feed.items.filter(
    (item) =>
      !query ||
      `${item.name} ${item.details} ${item.kind} ${item.location}`.toLowerCase().includes(query)
  );

  if (items.length === 0) {
    activityList.innerHTML = `<div class="empty">No activity matches that filter.</div>`;
    return;
  }

  activityList.innerHTML = items
    .map(
      (item) => `
      <article class="row">
        ${item.avatar ? `<img class="avatar" loading="lazy" src="${remoteImage(item.avatar)}" alt="" />` : `<span class="avatar"></span>`}
        <div class="body">
          <div class="title">
            <strong>${escape(item.name || "Someone")}</strong>
            <span class="kind">${escape(item.kind)}</span>
          </div>
          <div class="sub">${escape(item.details)}</div>
        </div>
        <span class="at">${escape(clock(item.at))}</span>
      </article>`
    )
    .join("");
}

// ---------------------------------------------------------------------- media

function renderMedia(reset = false) {
  if (reset) mediaList.innerHTML = "";
  const query = state.query.trim().toLowerCase();
  const items = state.media.filter(
    (entry) => !query || `${entry.world} ${entry.path}`.toLowerCase().includes(query)
  );
  mediaList.innerHTML = items
    .map(
      (entry) => `
      <figure class="tile">
        <img loading="lazy" src="${localImage(entry.path)}" alt="" />
        <figcaption>${escape(entry.world || "VRChat")} · ${escape(clock(entry.taken_at))}</figcaption>
      </figure>`
    )
    .join("");
  if (items.length === 0) {
    mediaList.innerHTML = `<div class="empty">No indexed pictures yet.</div>`;
  }
}

async function loadMedia(more = false) {
  if (state.mediaLoading) return;
  state.mediaLoading = true;
  try {
    const page = await invoke("media", { limit: 120, offset: more ? state.mediaOffset : 0 });
    state.media = more ? state.media.concat(page) : page;
    state.mediaOffset = more ? state.mediaOffset + page.length : page.length;
    el("media-more").hidden = page.length < 120;
    renderMedia(true);
  } catch (error) {
    mediaList.innerHTML = `<div class="empty">${escape(error)}</div>`;
  } finally {
    state.mediaLoading = false;
  }
}

// -------------------------------------------------------------- notifications

function renderNotifications() {
  const query = state.query.trim().toLowerCase();
  const items = state.notices.filter(
    (notice) => !query || `${notice.sender} ${notice.message} ${notice.kind}`.toLowerCase().includes(query)
  );
  if (items.length === 0) {
    noticeList.innerHTML = `<div class="empty">No notifications.</div>`;
    return;
  }
  noticeList.innerHTML = items
    .map(
      (notice) => `
      <article class="row ${notice.seen ? "" : "unread"}">
        <div class="body">
          <div class="title">
            <strong>${escape(notice.sender || notice.kind)}</strong>
            <span class="kind">${escape(notice.kind)}</span>
          </div>
          <div class="sub">${escape(notice.message)}</div>
        </div>
        <span class="at">${escape(clock(notice.created_at))}</span>
      </article>`
    )
    .join("");
}

async function loadNotifications() {
  if (!state.signedIn) {
    noticeList.innerHTML = `<div class="empty">Sign in to load notifications.</div>`;
    return;
  }
  try {
    state.notices = await invoke("notifications");
    renderNotifications();
  } catch (error) {
    noticeList.innerHTML = `<div class="empty">${escape(error)}</div>`;
  }
}

// --------------------------------------------------------------- history pane

function renderInstances() {
  if (state.instances.length === 0) {
    instanceList.innerHTML = `<div class="empty">No instance history yet.</div>`;
    return;
  }

  instanceList.innerHTML = state.instances
    .map(
      (row) => `
      <article class="row">
        <div class="body">
          <div class="title"><strong>${escape(row.world || row.location)}</strong></div>
          <div class="sub">${escape(row.location)}</div>
        </div>
        <span class="at">${escape(clock(row.last_seen))}</span>
      </article>`
    )
    .join("");
}

// ------------------------------------------------------------------- settings

let refreshTimer = 0;

function startRefreshTimer() {
  clearInterval(refreshTimer);
  const seconds = Math.max(15, Number(state.settings.refresh_seconds) || 60);
  refreshTimer = setInterval(() => {
    if (state.signedIn && document.visibilityState === "visible" && !state.busy) {
      loadFriends();
      if (state.tab === "instance") loadInstance();
    }
  }, seconds * 1000);
}

function fillSettings() {
  el("refresh-seconds").value = state.settings.refresh_seconds;
  el("show-offline").checked = state.settings.show_offline;
  el("animate-backdrop").checked = state.settings.animate_backdrop;
}

async function loadSettings() {
  state.settings = await invoke("settings_get");
  fillSettings();
  startRefreshTimer();
}

el("settings-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const next = {
    refresh_seconds: Number(el("refresh-seconds").value) || 60,
    show_offline: el("show-offline").checked,
    animate_backdrop: el("animate-backdrop").checked
  };
  try {
    await invoke("settings_save", { next });
    state.settings = next;
    fillSettings();
    startRefreshTimer();
    renderRoster();
    el("settings-message").textContent = "Saved.";
  } catch (error) {
    el("settings-message").textContent = String(error);
  }
});

// ----------------------------------------------------------------------- auth

signinForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  el("auth-error").textContent = "";
  setBusy(true, "Signing in to VRChat…");
  try {
    const result = await invoke("sign_in", {
      username: el("username").value,
      password: el("password").value
    });
    el("password").value = "";
    if (result.state === "two_factor") {
      const methods = (result.methods || []).join(", ");
      el("twofactor-hint").textContent = methods
        ? `VRChat asked for: ${methods}. Enter the code from your authenticator app.`
        : "Enter the code from your authenticator app.";
      signinForm.hidden = true;
      twoFactorForm.hidden = false;
      el("code").focus();
      status.textContent = "Waiting for the two-factor code.";
    } else {
      showUser(result.user);
      await loadFriends();
    }
  } catch (error) {
    el("auth-error").textContent = String(error);
    status.textContent = String(error);
  } finally {
    setBusy(false);
  }
});

twoFactorForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  el("twofactor-error").textContent = "";
  setBusy(true, "Verifying the code…");
  try {
    const user = await invoke("verify_two_factor", { code: el("code").value, method: "totp" });
    el("code").value = "";
    showUser(user);
    await loadFriends();
  } catch (error) {
    el("twofactor-error").textContent = String(error);
    status.textContent = String(error);
  } finally {
    setBusy(false);
  }
});

el("signout").addEventListener("click", async () => {
  await invoke("sign_out");
  state.user = null;
  state.notices = [];
  state.instance = null;
  accountLine.textContent = "signed out";
  showAuth();
  status.textContent = "Signed out of the shell. The other app's saved session is untouched.";
});

document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    state.tab = tab.dataset.tab;
    document.querySelectorAll(".tab").forEach((other) => other.classList.toggle("active", other === tab));
    document.querySelectorAll(".pane").forEach((pane) => pane.classList.toggle("active", pane.id === state.tab));
    search.placeholder = `Filter ${state.tab}…`;
    if (state.tab === "instance") loadInstance();
    if (state.tab === "favorites") loadFavorites();
    if (state.tab === "media" && state.media.length === 0) loadMedia();
    if (state.tab === "notifications") loadNotifications();
  });
});

search.addEventListener("input", () => {
  state.query = search.value;
  if (state.tab === "friends") renderRoster();
  else if (state.tab === "activity") renderActivity();
  else if (state.tab === "media") renderMedia(true);
  else if (state.tab === "notifications") renderNotifications();
});

el("refresh").addEventListener("click", () => {
  if (!state.signedIn) return;
  if (state.tab === "instance") loadInstance();
  else if (state.tab === "favorites") loadFavorites();
  else if (state.tab === "notifications") loadNotifications();
  else loadFriends();
});

el("media-more").addEventListener("click", () => loadMedia(true));

async function start() {
  try {
    await loadSettings();
    const [summary, feed, instances] = await Promise.all([
      invoke("summary"),
      invoke("feed", { account: null, limit: 400 }),
      invoke("instances", { limit: 80 })
    ]);

    state.feed = feed;
    state.instances = instances;
    renderChips(summary);
    renderActivity();
    renderInstances();

    const auth = await invoke("auth_state");
    if (auth.signed_in && auth.user) {
      showUser(auth.user);
      await loadFriends();
    } else {
      showAuth();
      accountLine.textContent = "not signed in";
      status.textContent = "Rust core ready — sign in to load friends.";
    }
    renderInstance();
  } catch (error) {
    status.textContent = `Rust core error: ${error}`;
  }
}

el("check-update").addEventListener("click", async () => {
  const message = el("update-message");
  const install = el("install-update");
  message.textContent = "Checking…";
  install.hidden = true;
  try {
    const status = await invoke("check_update");
    message.textContent = status.notes ? `${status.message} ${status.notes}` : status.message;
    install.hidden = !status.available;
  } catch (error) {
    message.textContent = `Update check failed: ${error}`;
  }
});

el("install-update").addEventListener("click", async () => {
  const message = el("update-message");
  message.textContent = "Downloading… the app restarts once the update is installed.";
  try {
    await invoke("install_update");
  } catch (error) {
    message.textContent = `Install failed: ${error}`;
  }
});

el("chatbox-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const message = el("chatbox-message");
  const text = el("chatbox-text").value.trim();
  if (!text) {
    message.textContent = "Type a message first.";
    return;
  }
  message.textContent = "Sending…";
  try {
    message.textContent = await invoke("send_chatbox", {
      message: text,
      send: !el("chatbox-wait").checked
    });
  } catch (error) {
    message.textContent = String(error);
  }
});

el("merge-activity").addEventListener("click", async () => {
  const message = el("merge-message");
  message.textContent = "Importing…";
  try {
    message.textContent = await invoke("merge_activity");
  } catch (error) {
    message.textContent = String(error);
  }
});

start();
