// Reads the running shell's rendered friend rows through the WebView2 debug port.
const targets = await (await fetch("http://127.0.0.1:9222/json")).json();
const page = targets.find((target) => target.type === "page");
if (!page) {
  console.log("no page target");
  process.exit(1);
}
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = reject;
});
const evaluate = (id, expression) =>
  new Promise((resolve) => {
    const handler = (event) => {
      const message = JSON.parse(event.data);
      if (message.id !== id) return;
      socket.removeEventListener("message", handler);
      resolve(message.result?.result?.value ?? message.error ?? null);
    };
    socket.addEventListener("message", handler);
    socket.send(
      JSON.stringify({
        id,
        method: "Runtime.evaluate",
        params: { expression, returnByValue: true, awaitPromise: true },
      }),
    );
  });

const summary = await evaluate(
  1,
  `(() => {
    const rows = [...document.querySelectorAll("#friends .friend, #friends li, .friend-row")]
      .map((row) => row.innerText.replace(/\\s+/g, " ").trim())
      .filter(Boolean);
    const online = rows.filter((row) => row.length > 0);
    return {
      rows: online.length,
      rawIds: online.filter((row) => /wrld_[0-9a-f-]{8,}/i.test(row)).length,
      sample: online.slice(0, 6),
      status: document.body.innerText.split("\\n").filter((l) => l.trim()).slice(0, 4),
    };
  })()`,
);
console.log(JSON.stringify(summary, null, 2));
socket.close();
