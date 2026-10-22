import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import fs from "node:fs";
import os from "node:os";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { startMockRhino, TINY_PNG, type MockRhino } from "./mock-rhino.js";
import { ALL_TOOL_NAMES } from "../src/tools.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const SERVER = path.resolve(here, "..", "dist", "index.js");

type TextBlock = { type: "text"; text: string };
type ImageBlock = { type: "image"; data: string; mimeType: string };
type Result = { content: (TextBlock | ImageBlock)[]; isError?: boolean };

async function connect(env: Record<string, string>): Promise<Client> {
  const client = new Client({ name: "e2e", version: "1.0.0" });
  const baseEnv: Record<string, string> = {};
  for (const [k, v] of Object.entries(process.env)) if (v !== undefined && !k.startsWith("OPEN_RHINO_MCP_")) baseEnv[k] = v;
  const transport = new StdioClientTransport({ command: process.execPath, args: [SERVER], env: { ...baseEnv, ...env }, stderr: "pipe" });
  await client.connect(transport);
  return client;
}

function text(r: Result): string {
  return r.content.filter((c): c is TextBlock => c.type === "text").map((c) => c.text).join("\n");
}

let mock: MockRhino;
let client: Client;

before(async () => {
  assert.ok(fs.existsSync(SERVER), "run npm run build first");
  mock = await startMockRhino();
  client = await connect({ OPEN_RHINO_MCP_SESSION_FILE: mock.sessionFile });
});

after(async () => {
  await client?.close();
  await mock?.close();
});

test("lists all 39 tools with annotations", async () => {
  const { tools } = await client.listTools();
  const names = tools.map((t) => t.name).sort();
  assert.equal(ALL_TOOL_NAMES.length, 39);
  assert.deepEqual(names, [...ALL_TOOL_NAMES].sort());
  for (const t of tools) {
    assert.ok(t.description && t.description.length > 30, `${t.name} needs a description`);
    assert.ok(t.annotations, `${t.name} needs annotations`);
  }
  const q = tools.find((t) => t.name === "rhino_query_objects")!;
  assert.equal(q.annotations?.readOnlyHint, true);
  const del = tools.find((t) => t.name === "rhino_delete_objects")!;
  assert.equal(del.annotations?.destructiveHint, true);
});

test("server exposes instructions, guide resources and prompts", async () => {
  assert.match(client.getInstructions() ?? "", /IronPython 2\.7/);
  const { resources } = await client.listResources();
  assert.ok(resources.some((r) => r.uri === "rhino7://guide/scripting_commands"));
  const res = await client.readResource({ uri: "rhino7://guide/python" });
  assert.match(String((res.contents[0] as { text: string }).text), /f-strings/);
  const { prompts } = await client.listPrompts();
  assert.deepEqual(prompts.map((p) => p.name).sort(), ["draft_check", "schedule_from_tags"]);
});

test("rhino_status forwards to system.status", async () => {
  const r = (await client.callTool({ name: "rhino_status", arguments: {} })) as Result;
  assert.ok(!r.isError);
  assert.equal(JSON.parse(text(r)).units, "feet");
  assert.equal(mock.calls.at(-1)?.method, "system.status");
});

test("rhino_query_objects passes filters through untouched", async () => {
  const r = (await client.callTool({
    name: "rhino_query_objects",
    arguments: { layer: "E-POWR", user_text: { circuit: "11" }, limit: 10 },
  })) as Result;
  assert.ok(!r.isError);
  const call = mock.calls.at(-1)!;
  assert.equal(call.method, "objects.query");
  assert.deepEqual(call.params, { layer: "E-POWR", user_text: { circuit: "11" }, limit: 10 });
  assert.equal(JSON.parse(text(r)).objects[0].user_text.circuit, "11");
});

test("rhino_create_geometry validates items and returns ids", async () => {
  const r = (await client.callTool({
    name: "rhino_create_geometry",
    arguments: {
      items: [
        { type: "line", start: [0, 0], end: [10, 0, 0], attributes: { layer: "A::B", user_text: { tag: "x" } } },
        { type: "circle", radius: 2, center: [5, 5, 0] },
      ],
    },
  })) as Result;
  assert.ok(!r.isError, text(r));
  assert.equal(JSON.parse(text(r)).ids.length, 2);

  const bad = (await client.callTool({ name: "rhino_create_geometry", arguments: { items: [{ type: "circle" }] } })) as Result;
  assert.equal(bad.isError, true, "missing radius should be rejected by the schema");
});

test("rhino_capture_view returns a PNG image", async () => {
  const r = (await client.callTool({ name: "rhino_capture_view", arguments: { view: "Layout 1", width: 800 } })) as Result;
  assert.ok(!r.isError);
  const img = r.content.find((c): c is ImageBlock => c.type === "image");
  assert.ok(img);
  assert.equal(img!.mimeType, "image/png");
  assert.equal(img!.data, TINY_PNG);
  assert.match(text(r), /Layout 1/);
});

test("python errors come back as tool errors with traceback", async () => {
  const good = (await client.callTool({ name: "rhino_run_python", arguments: { code: "print('hello')\nresult = {'n': 2}" } })) as Result;
  assert.ok(!good.isError);
  assert.equal(JSON.parse(text(good)).result.n, 2);
  const bad = (await client.callTool({ name: "rhino_run_python", arguments: { code: "raise Exception('boom')" } })) as Result;
  assert.equal(bad.isError, true);
  assert.match(text(bad), /boom/);
  assert.match(text(bad), /line 1/);
});

test("plug-in error codes map to readable tool errors", async () => {
  const busy = (await client.callTool({ name: "rhino_run_command", arguments: { macro: "_Line" } })) as Result;
  assert.equal(busy.isError, true);
  assert.match(text(busy), /busy/i);
  const nf = (await client.callTool({ name: "rhino_delete_layer", arguments: { path: "Nope" } })) as Result;
  assert.equal(nf.isError, true);
  assert.match(text(nf), /Not found: Layer "Nope" not found/);
  const unknown = (await client.callTool({ name: "rhino_list_blocks", arguments: {} })) as Result;
  assert.equal(unknown.isError, true);
  assert.match(text(unknown), /doesn't support/);
});

test("rhino_list_commands forwards filters", async () => {
  const r = (await client.callTool({ name: "rhino_list_commands", arguments: { filter: "Hatch" } })) as Result;
  assert.ok(!r.isError);
  assert.deepEqual(JSON.parse(text(r)).commands, ["Hatch", "HatchBase"]);
});

test("rhino_guide and rhino_command_help work without Rhino", async () => {
  const g = (await client.callTool({ name: "rhino_guide", arguments: {} })) as Result;
  assert.match(text(g), /Order of preference/);
  const t = (await client.callTool({ name: "rhino_guide", arguments: { topic: "drafting" } })) as Result;
  assert.match(text(t), /detail/i);
});

test("wrong token gives an unauthorized tool error", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "orm-bad-"));
  const sessionFile = path.join(dir, "session.json");
  fs.writeFileSync(sessionFile, JSON.stringify({ port: mock.port, token: "wrong" }));
  const c = await connect({ OPEN_RHINO_MCP_SESSION_FILE: sessionFile });
  try {
    const r = (await c.callTool({ name: "rhino_status", arguments: {} })) as Result;
    assert.equal(r.isError, true);
    assert.match(text(r), /token/i);
  } finally {
    await c.close();
  }
});

test("unreachable Rhino gives the friendly not-connected error", async () => {
  const c = await connect({ OPEN_RHINO_MCP_SESSION_FILE: path.join(os.tmpdir(), "does-not-exist", "session.json") });
  try {
    const r = (await c.callTool({ name: "rhino_status", arguments: {} })) as Result;
    assert.equal(r.isError, true);
    assert.match(text(r), /Rhino isn't connected/);
    const dead = (await c.callTool({ name: "rhino_status", arguments: {} })) as Result;
    assert.equal(dead.isError, true);
  } finally {
    await c.close();
  }
  // Port with nothing listening.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "orm-dead-"));
  const sessionFile = path.join(dir, "session.json");
  fs.writeFileSync(sessionFile, JSON.stringify({ port: 1, token: "t" }));
  const c2 = await connect({ OPEN_RHINO_MCP_SESSION_FILE: sessionFile });
  try {
    const r = (await c2.callTool({ name: "rhino_status", arguments: {} })) as Result;
    assert.equal(r.isError, true);
    assert.match(text(r), /Rhino isn't connected/);
  } finally {
    await c2.close();
  }
});

test("reconnects after Rhino restarts with a new port and token", async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "orm-restart-"));
  const first = await startMockRhino({ token: "first", dir });
  const c = await connect({ OPEN_RHINO_MCP_SESSION_FILE: first.sessionFile });
  try {
    assert.ok(!((await c.callTool({ name: "rhino_status", arguments: {} })) as Result).isError);
    await first.close();
    const second = await startMockRhino({ token: "second", dir }); // rewrites the same session file
    try {
      const r = (await c.callTool({ name: "rhino_status", arguments: {} })) as Result;
      assert.ok(!r.isError, text(r));
      assert.equal(second.calls.at(-1)?.method, "system.status");
    } finally {
      await second.close();
    }
  } finally {
    await c.close();
  }
});
