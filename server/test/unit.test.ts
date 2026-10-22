import { test } from "node:test";
import assert from "node:assert/strict";
import { RhinoBridge, BridgeError, resolveEndpoint } from "../src/bridge.js";
import { htmlToText, fetchCommandHelp, commandHelpUrl, clearHelpCache, type FetchLike } from "../src/docs.js";
import { renderGuide, GUIDE_TOPICS } from "../src/guide.js";
import { startMockRhino } from "./mock-rhino.js";

test("bridge enforces per-request timeout", async () => {
  const mock = await startMockRhino();
  const bridge = new RhinoBridge({ env: { OPEN_RHINO_MCP_SESSION_FILE: mock.sessionFile } });
  try {
    await assert.rejects(bridge.call("commands.list", { filter: "slow" }, 300), (e: BridgeError) => e.code === "timeout");
    // The connection stays usable after a timeout.
    const ok = await bridge.call<{ count: number }>("commands.list", {});
    assert.equal(ok.count, 2);
  } finally {
    bridge.close();
    await mock.close();
  }
});

test("env overrides beat the session file", () => {
  const ep = resolveEndpoint({ OPEN_RHINO_MCP_PORT: "5555", OPEN_RHINO_MCP_TOKEN: "abc", OPEN_RHINO_MCP_SESSION_FILE: "/nonexistent" });
  assert.deepEqual([ep.port, ep.token, ep.host], [5555, "abc", "127.0.0.1"]);
  assert.throws(() => resolveEndpoint({ OPEN_RHINO_MCP_SESSION_FILE: "/nonexistent" }), /isn't connected/);
});

test("command help URL normalizes names", () => {
  assert.equal(commandHelpUrl("-_Hatch"), "https://docs.mcneel.com/rhino/7/help/en-us/commands/hatch.htm");
  assert.equal(commandHelpUrl("Make2D"), "https://docs.mcneel.com/rhino/7/help/en-us/commands/make2d.htm");
});

test("htmlToText keeps headings, lists and table rows", () => {
  const html = `<html><head><title>x</title><script>var a=1</script></head><body><nav>menu</nav>
  <h1>Hatch</h1><p>Fills a closed curve&nbsp;with a pattern.</p>
  <h2>Command-line options</h2><table><tr><td><b>Pattern</b></td><td>Selects the hatch pattern.</td></tr>
  <tr><td>Scale</td><td>Pattern scale.</td></tr></table><ul><li>One</li><li>Two</li></ul></body></html>`;
  const t = htmlToText(html);
  assert.match(t, /^# Hatch/);
  assert.match(t, /## Command-line options/);
  assert.match(t, /\*\*Pattern\*\* \| Selects the hatch pattern\./);
  assert.match(t, /- One\n- Two/);
  assert.doesNotMatch(t, /var a=1|menu/);
});

test("fetchCommandHelp caches and reports 404s", async () => {
  clearHelpCache();
  let calls = 0;
  const fake: FetchLike = async (url) => {
    calls++;
    if (url.endsWith("nope.htm")) return { ok: false, status: 404, text: async () => "" };
    return { ok: true, status: 200, text: async () => "<body><h1>Hatch</h1><p>Body</p></body>" };
  };
  const a = await fetchCommandHelp("Hatch", fake);
  const b = await fetchCommandHelp("_Hatch", fake);
  assert.equal(a, b);
  assert.equal(calls, 1);
  assert.match(a, /Source: https:\/\/docs\.mcneel\.com/);
  await assert.rejects(fetchCommandHelp("Nope", fake), /rhino_list_commands/);
});

test("every guide topic renders", () => {
  for (const t of GUIDE_TOPICS) {
    const s = renderGuide(t);
    assert.ok(s.length > 400, t);
  }
  assert.match(renderGuide("bogus"), /Unknown topic/);
});
