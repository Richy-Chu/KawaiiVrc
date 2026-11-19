const invoke = window.__TAURI__.core.invoke;

const state = { feed: null, instances: [], query: "" };
const activityList = document.getElementById("activity-list");
const instanceList = document.getElementById("instance-list");
const status = document.getElementById("status");
const search = document.getElementById("search");
const chips = document.getElementById("chips");
const account = document.getElementById("account");

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

function renderChips(summary) {
  chips.innerHTML = [
    `<span class="chip"><b>${summary.accounts}</b> accounts</span>`,
    `<span class="chip">activity <b>${bytes(summary.activity_bytes)}</b></span>`,
    `<span class="chip">history <b>${bytes(summary.history_bytes)}</b></span>`,
    `<span class="chip">media index <b>${bytes(summary.media_bytes)}</b></span>`,
    `<span class="chip">avatars <b>${bytes(summary.friend_bytes)}</b></span>`
  ].join("");
}

function renderActivity() {
  if (!state.feed) return;
  const query = state.query.trim().toLowerCase();
  const items = state.feed.items.filter((item) =>
    !query ||
    `${item.name} ${item.details} ${item.kind} ${item.location}`.toLowerCase().includes(query));

  if (items.length === 0) {
    activityList.innerHTML = `<div class="empty">No activity matches that filter.</div>`;
    return;
  }

  activityList.innerHTML = items
    .map(
      (item) => `
      <article class="row">
        ${item.avatar ? `<img class="avatar" loading="lazy" src="${escape(item.avatar)}" alt="" />` : `<span class="avatar"></span>`}
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

document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach((other) => other.classList.toggle("active", other === tab));
    document.querySelectorAll(".pane").forEach((pane) => pane.classList.toggle("active", pane.id === tab.dataset.tab));
  });
});

search.addEventListener("input", () => {
  state.query = search.value;
  renderActivity();
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
    account.textContent = `${feed.total.toLocaleString()} activity entries · account ${feed.account}`;
    renderActivity();
    renderInstances();
    status.textContent = "Rust core · WebView2 UI · read-only stage 1 of the rewrite";
  } catch (error) {
    status.textContent = `Rust core error: ${error}`;
    activityList.innerHTML = `<div class="empty">${escape(error)}</div>`;
  }
}

start();
