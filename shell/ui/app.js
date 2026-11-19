const invoke = window.__TAURI__.core.invoke;

const state = {
  signedIn: false,
  user: null,
  friends: [],
  feed: null,
  instances: [],
  query: "",
  busy: false
};

const el = (id) => document.getElementById(id);
const activityList = el("activity-list");
const instanceList = el("instance-list");
const roster = el("roster");
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

const avatar = (url) =>
  url ? `http://kawaii.localhost/avatar?u=${encodeURIComponent(url)}` : "";

const dotClass = (status) => `dot-${String(status || "offline").replace(/\s+/g, "-")}`;

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
    el("me").src = avatar(user.avatar);
  } else {
    el("me").removeAttribute("src");
  }
}

function renderChips(summary) {
  chips.innerHTML = [
    `<span class="chip"><b>${summary.accounts}</b> accounts</span>`,
    `<span class="chip">activity <b>${bytes(summary.activity_bytes)}</b></span>`,
    `<span class="chip">history <b>${bytes(summary.history_bytes)}</b></span>`,
    `<span class="chip">media index <b>${bytes(summary.media_bytes)}</b></span>`
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
      ${friend.avatar ? `<img class="avatar" loading="lazy" src="${avatar(friend.avatar)}" alt="" />` : `<span class="avatar"></span>`}
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

  const inGame = matches.filter((friend) => friend.online && friend.world && friend.world.startsWith("wrld_"));
  const online = matches.filter((friend) => friend.online && !inGame.includes(friend));
  const offline = matches.filter((friend) => !friend.online);
  const byName = (a, b) => a.name.localeCompare(b.name);

  // A few thousand offline rows in the DOM costs more than it shows; the
  // search box reaches every friend, the collapsed list shows the first 300.
  const offlineShown = query ? offline.slice(0, 600) : offline.slice(0, 300);

  const groups = [
    ["In game", inGame.sort(byName), true],
    ["Online", online.sort(byName), false],
    [
      `Offline (${offline.length})`,
      offlineShown.sort(byName),
      false
    ]
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

async function loadFriends() {
  if (!state.signedIn || state.busy) return;
  setBusy(true, "Loading friends from VRChat…");
  try {
    const result = await invoke("friends");
    state.friends = result.items;
    state.live = result.live;
    accountLine.textContent = `${result.total.toLocaleString()} friends · ${result.online} online · ${state.user?.display_name ?? ""}`;
    renderRoster();
    status.textContent = result.live
      ? "Live presence from VRChat's realtime pipeline."
      : "Roster loaded; presence is reconnecting.";
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
        ${item.avatar ? `<img class="avatar" loading="lazy" src="${avatar(item.avatar)}" alt="" />` : `<span class="avatar"></span>`}
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

function renderActive() {
  if (state.query.length > 0) {
    renderRoster();
    renderActivity();
  }
}

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
    const user = await invoke("verify_two_factor", {
      code: el("code").value,
      method: "totp"
    });
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
  accountLine.textContent = "signed out";
  showAuth();
  status.textContent = "Signed out of the shell. The WPF app's saved session is untouched.";
});

document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    document
      .querySelectorAll(".tab")
      .forEach((other) => other.classList.toggle("active", other === tab));
    document
      .querySelectorAll(".pane")
      .forEach((pane) => pane.classList.toggle("active", pane.id === tab.dataset.tab));
    search.placeholder = tab.dataset.tab === "friends" ? "Filter friends…" : "Filter…";
  });
});

search.addEventListener("input", () => {
  state.query = search.value;
  renderActive();
});

el("refresh").addEventListener("click", () => {
  if (state.signedIn) {
    loadFriends();
  }
});

async function start() {
  try {
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
  } catch (error) {
    status.textContent = `Rust core error: ${error}`;
  }
}

// Refreshing the roster while the window is visible keeps it close to live
// without hammering VRChat when the app sits in the background.
setInterval(() => {
  if (state.signedIn && document.visibilityState === "visible" && !state.busy) {
    loadFriends();
  }
}, 60000);

start();
