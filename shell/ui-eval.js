// Evaluates one expression in the running shell's WebView and prints the result.
const expression = process.argv[2];
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
const result = await new Promise((resolve) => {
  socket.addEventListener("message", (event) => {
    const message = JSON.parse(event.data);
    if (message.id !== 1) return;
    resolve(message.result?.result ?? message.error ?? null);
  });
  socket.send(
    JSON.stringify({
      id: 1,
      method: "Runtime.evaluate",
      params: { expression, returnByValue: true, awaitPromise: true },
    }),
  );
});
console.log(JSON.stringify(result, null, 2));
socket.close();
