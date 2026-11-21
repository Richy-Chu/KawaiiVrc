// Notification paging and refresh stitching, checked against the real
// shell/ui/app.js. No Tauri window, no VRChat account and no network: app.js is
// a classic script, so it runs in a vm context with a stub DOM, and the tests
// drive `applyNoticeOverview`, `loadMoreNotices`, `refreshNotices` and the
// list's click handler just as the page does.
//
//   node --test shell/ui-tests
//
// Three row-loss bugs lived here before: a page stitched onto the loaded rows
// by position after an arrival, an answered notice left in the list shifting
// the next offset, and a failed refresh sitting out the full minute. Every
// case below exists so one of them cannot come back unnoticed.
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const appSource = fs.readFileSync(path.join(here, "..", "ui", "app.js"), "utf8");

const notice = (id) => ({
  id,
  kind: "friendRequest",
  sender: "Someone",
  message: "hello",
  created_at: 0,
  seen: true
});
// n0, n1, ... — ids sort the way the server does, newest first.
const rows = (from, count) => Array.from({ length: count }, (_, index) => `n${from + index}`);

async function shell() {
  const clock = { now: 1_700_000_000_000 };
  class ClockDate extends Date {
    static now() {
      return clock.now;
    }
  }

  // Only what the notification pane touches: listeners it attaches have to stay
  // reachable, and the fields its rows and badge write have to hold a value. The
  // tab wiring at load only needs querySelectorAll to answer with nothing.
  const elements = new Map();
  const element = (id) => {
    if (elements.has(id)) return elements.get(id);
    const listeners = {};
    const node = {
      hidden: false,
      disabled: false,
      value: "",
      textContent: "",
      innerHTML: "",
      title: "",
      listeners,
      addEventListener: (name, fn) => (listeners[name] = listeners[name] || []).push(fn)
    };
    elements.set(id, node);
    return node;
  };
  const document = { getElementById: element, querySelectorAll: () => [], visibilityState: "visible" };

  // A stand-in for VRChat's notification list: positional paging, newest first.
  const server = { ids: [], fail: false, requests: [] };
  let live = false;
  const reply = async (command, args) => {
    // The page calls start() on load. Leave it parked until a test serves data,
    // so its setup requests cannot race a test that is arranging state.
    if (!live) return new Promise(() => {});
    server.requests.push({ command, args });
    if (command === "notification_overview") {
      if (server.fail) throw new Error("VRChat is rate limiting.");
      const offset = args?.offset ?? 0;
      const slice = server.ids.slice(offset, offset + 100);
      return { notices: slice.map((id) => notice(id)), waiting: server.ids.length, offset, has_more: slice.length === 100 };
    }
    if (command === "accept_notification" || command === "dismiss_notification") {
      const at = server.ids.indexOf(args.noticeId);
      if (at >= 0) server.ids.splice(at, 1);
      const slice = server.ids.slice(0, 100);
      return { notices: slice.map((id) => notice(id)), waiting: server.ids.length, offset: 0, has_more: slice.length === 100 };
    }
    return {};
  };
  const window = { __TAURI__: { core: { invoke: (command, args) => reply(command, args) } } };
  // A vm context brings the standard builtins itself, so only the host-side
  // objects the page reaches for are supplied here; `Date` is the fake clock.
  const context = vm.createContext({ window, document, console, Date: ClockDate });
  vm.runInContext(appSource, context, { filename: "shell/ui/app.js" });

  const run = (expression) => vm.runInContext(expression, context);
  const ids = () => JSON.parse(run("JSON.stringify(state.notices.map((item) => item.id))"));
  const state = (name) => run(`state.${name}`);
  const signIn = () => run("state.signedIn = true");
  const serve = (list) => {
    server.ids = [...list];
    server.fail = false;
    live = true;
  };
  // The rows a refresh starts from, without going through paging.
  const loaded = (list) => {
    run(`state.notices = ${JSON.stringify(list.map((id) => notice(id)))}`);
  };
  // One refreshed first page, applied the way refreshNotices applies it.
  const stitch = (list) => {
    const page = { notices: list.map((id) => notice(id)), waiting: 0, offset: 0, has_more: true };
    run(`applyNoticeOverview(${JSON.stringify(page)})`);
    return ids();
  };
  const click = (action, id, kind = "friendRequest") => {
    const button = { dataset: { action, id, kind }, disabled: false };
    const handlers = element("notification-list").listeners.click || [];
    assert.equal(handlers.length, 1, "the notification list has its click handler");
    return handlers[0]({ target: { closest: () => button } });
  };
  const overviewCalls = () => server.requests.filter((request) => request.command === "notification_overview");
  const offsets = () => overviewCalls().map((request) => request.args?.offset ?? 0);
  const logs = () =>
    server.requests.filter((request) => request.command === "notice_log").map((request) => request.args.line);
  return { run, ids, state, signIn, serve, loaded, stitch, click, overviewCalls, offsets, logs, element, clock, server };
}

// ------------------------------------------------------------ refresh stitch

test("a refresh keeps every loaded row when notices arrive at the top", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch(["a0", "a1", ...rows(0, 98)]), ["a0", "a1", ...rows(0, 150)]);
});

test("a refresh drops rows answered elsewhere and keeps the rest once, in order", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch(rows(5, 100)), rows(5, 145));
});

test("a row answered inside the refreshed page is not shown twice", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch([...rows(0, 50), ...rows(51, 50)]), [...rows(0, 50), ...rows(51, 99)]);
});

test("a refresh with arrivals and removals together still leaves one window in order", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  const gone = ["n10", "n20", "n30"];
  const out = h.stitch(["b0", ...rows(0, 100).filter((id) => !gone.includes(id))]);
  assert.deepEqual(out, ["b0", ...rows(0, 150).filter((id) => !gone.includes(id))]);
});

test("a page that shares no row with the window replaces it instead of guessing", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch(rows(200, 100)), rows(200, 100));
  assert.deepEqual(h.logs(), [], "a page applied while the page is not live is not logged");
});

test("a first page that meets nothing leaves one line in the shell log", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(200, 100));
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch(rows(200, 100)), rows(200, 100));
  assert.deepEqual(h.logs(), [
    "first page did not meet the window offset=0 page=100 window=150 waiting=0 first=n200 tail=n149"
  ]);
});

test("an empty first page empties the list", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch([]), []);
});

test("an unchanged refresh keeps the depth already loaded", async () => {
  const h = await shell();
  h.loaded(rows(0, 150));
  assert.deepEqual(h.stitch(rows(0, 100)), rows(0, 150));
});

test("a page deeper than a short window replaces it whole", async () => {
  const h = await shell();
  h.loaded(rows(0, 40));
  assert.deepEqual(h.stitch(rows(0, 100)), rows(0, 100));
});

// -------------------------------------------------------------------- paging

test("load more starts behind the visible end and stitches repeats by id", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 250));
  await h.run("loadNotifications()");
  assert.deepEqual(h.ids(), rows(0, 100));

  await h.run("loadMoreNotices()");
  const overlap = h.run("NOTICE_OVERLAP");
  assert.deepEqual(h.offsets(), [0, 100 - overlap]);
  // The page starts five rows back, so a full page ends at n194, not n199.
  assert.deepEqual(h.ids(), rows(0, 195));
});

test("a notice arriving after a refresh cannot hide a row from load more", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 250));
  await h.run("loadNotifications()");
  h.server.ids.unshift("arrival0");
  h.run("state.noticesAt = 0");
  await h.run("refreshNotices()");
  assert.deepEqual(h.ids(), ["arrival0", ...rows(0, 100)]);

  await h.run("loadMoreNotices()");
  assert.deepEqual(h.ids(), ["arrival0", ...rows(0, 195)]);
  assert.deepEqual(h.offsets(), [0, 0, 101 - h.run("NOTICE_OVERLAP")], "a refresh stays one request");
});

test("load more reaches the rows below removals that shifted the list", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 400));
  await h.run("loadNotifications()");
  h.server.ids.splice(10, 3); // n10..n12 answered in VRChat
  await h.run("loadMoreNotices()");
  assert.deepEqual(h.ids(), rows(0, 198));
});

test("load more walks back to the window after 30 notices are answered in VRChat", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 400));
  await h.run("loadNotifications()");
  h.server.ids.splice(10, 30); // more than the overlap covers, so the offset lands past the seam
  await h.run("loadMoreNotices()");
  assert.deepEqual(h.ids(), rows(0, 130), "the rows between the window and the page are not skipped");
  assert.deepEqual(h.offsets(), [0, 95, 0], "the page that met nothing is walked back from");
  assert.equal(h.element("notice-more").hidden, false, "paging continues past the walk");
  assert.deepEqual(h.logs(), [], "a walk that found the window is not a gap");
});

test("a walk that gives up restarts the window from its page and says so once", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 400));
  await h.run("loadNotifications()");
  h.server.ids.splice(0, 100); // every loaded row answered, so no page can meet them
  await h.run("loadMoreNotices()");
  assert.deepEqual(h.ids(), rows(100, 100), "the window starts over at the page instead of keeping a hole");
  assert.deepEqual(h.logs(), [
    "load-more walk gave up without meeting the window offset=0 page=100 window=100 waiting=300 first=n100 tail=n99"
  ], "the restart does not log the same gap twice");
  assert.equal(h.element("notice-more").hidden, false, "paging continues from the new window");
});

test("a load more that only repeats rows stops offering more", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 100));
  await h.run("loadNotifications()");
  assert.equal(h.element("notice-more").hidden, false);

  await h.run("loadMoreNotices()");
  assert.deepEqual(h.ids(), rows(0, 100));
  assert.equal(h.state("noticesHasMore"), false);
  assert.equal(h.element("notice-more").hidden, true);
});

test("load more does not fire while a notification request is in flight", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 250));
  await h.run("loadNotifications()");
  h.run("state.noticesBusy = true");
  const before = h.overviewCalls().length;
  await h.run("loadMoreNotices()");
  assert.equal(h.overviewCalls().length, before);
  h.run("state.noticesBusy = false");
});

// ------------------------------------------------- answering and refreshing

test("answering a notice drops its row here too, so the window continues below it", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 250));
  await h.run("loadNotifications()");
  await h.run("loadMoreNotices()");
  assert.deepEqual(h.ids(), rows(0, 195));

  await h.click("accept", "n50");
  const out = h.ids();
  assert.deepEqual(out, [...rows(0, 50), ...rows(51, 144)]);
  assert.equal(h.state("noticeTotal"), 249);
});

test("dismissing an invite drops its row too", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 150));
  await h.run("loadNotifications()");
  await h.click("dismiss", "n7", "invite");
  // The dismissal pulls n100 up into the refreshed page, and the window is
  // exactly that page: the answered row is gone and nothing is repeated.
  assert.deepEqual(h.ids(), [...rows(0, 7), ...rows(8, 93)]);
});

test("a failed refresh keeps the list and retries in 15 seconds, a good one waits the minute", async () => {
  const h = await shell();
  h.signIn();
  h.serve(rows(0, 150));
  await h.run("loadNotifications()");
  assert.equal(h.overviewCalls().length, 1);

  h.server.fail = true;
  h.run(`state.noticesAt = ${h.clock.now - h.run("NOTICE_REFRESH_MS")}`);
  await h.run("refreshNotices()");
  assert.deepEqual(h.ids(), rows(0, 100), "a failed refresh leaves the rows alone");
  assert.equal(h.overviewCalls().length, 2);
  const retry = h.run("NOTICE_REFRESH_MS") - h.run("NOTICE_RETRY_MS");
  assert.equal(h.state("noticesAt"), h.clock.now - retry);

  h.server.fail = false;
  h.clock.now += h.run("NOTICE_RETRY_MS") - 1_000;
  await h.run("refreshNotices()");
  assert.equal(h.overviewCalls().length, 2, "still inside the retry floor");

  h.clock.now += 1_000;
  await h.run("refreshNotices()");
  assert.equal(h.overviewCalls().length, 3, "the next tick after the retry floor does try");
  assert.deepEqual(h.ids(), rows(0, 100));

  h.clock.now += h.run("NOTICE_REFRESH_MS") - 1_000;
  await h.run("refreshNotices()");
  assert.equal(h.overviewCalls().length, 3, "a good refresh waits the full minute");

  h.clock.now += 1_000;
  await h.run("refreshNotices()");
  assert.equal(h.overviewCalls().length, 4);
});
