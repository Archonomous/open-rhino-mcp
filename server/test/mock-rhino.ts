// A fake Open Rhino MCP plug-in: NDJSON over TCP with token auth (docs/PROTOCOL.md).
import net from "node:net";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

// 1x1 transparent PNG.
export const TINY_PNG =
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

export interface MockRhino {
  port: number;
  token: string;
  sessionFile: string;
  calls: { method: string; params: Record<string, unknown> }[];
  close(): Promise<void>;
}

type Handler = (params: Record<string, unknown>) => unknown | Promise<unknown>;

class MockError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

const handlers: Record<string, Handler> = {
  "system.hello": () => ({ protocol: 1, plugin_version: "0.1.0-mock", rhino_version: "7.38.0", platform: "mock", methods: Object.keys(handlers) }),
  "system.status": () => ({
    rhino_version: "7.38.24338.17001",
    doc_name: "E1.3dm",
    doc_path: "C:\\Projects\\Casita\\E1.3dm",
    modified: false,
    units: "feet",
    abs_tolerance: 0.001,
    angle_tolerance_deg: 1,
    object_count: 412,
    layer_count: 18,
    current_layer: "E-POWR::Receptacles",
    active_view: "Top",
    command_running: false,
  }),
  "objects.query": (p) => ({
    total: 1,
    count: 1,
    offset: 0,
    objects: [
      {
        id: "6f1c2a6e-0000-4000-8000-000000000001",
        type: "block_instance",
        layer: (p.layer as string) ?? "E-POWR::Receptacles",
        name: "R-11a",
        color: "#000000",
        color_source: "layer",
        visible: true,
        locked: false,
        selected: false,
        bbox: { min: [0, 0, 0], max: [1, 1, 0] },
        user_text: { circuit: "11", VA: "180" },
        groups: [],
        block: "Receptacle",
      },
    ],
  }),
  "geometry.create": (p) => ({
    ids: (p.items as unknown[]).map((_, i) => `00000000-0000-4000-8000-${String(i).padStart(12, "0")}`),
    errors: [],
    created_ids: [],
    deleted_ids: [],
    modified_ids: [],
  }),
  "views.capture": (p) => ({ view: (p.view as string) ?? "Top", width: 1, height: 1, png_base64: TINY_PNG }),
  "python.run": (p) => {
    const code = String(p.code);
    if (code.includes("raise")) {
      return { ok: false, output: "partial\n", error: "Exception: boom", traceback: 'line 1, in <module>\nException: boom', created_ids: [], deleted_ids: [], modified_ids: [] };
    }
    return { ok: true, output: "hello\n", result: { n: 2 }, created_ids: [], deleted_ids: [], modified_ids: [] };
  },
  "commands.run": () => {
    throw new MockError("rhino_busy", "A command is running: _Line");
  },
  "layers.delete": () => {
    throw new MockError("not_found", 'Layer "Nope" not found');
  },
  "commands.list": async (p) => {
    if (p.filter === "slow") await new Promise((r) => setTimeout(r, 3000));
    return { count: 2, commands: ["Hatch", "HatchBase"] };
  },
};

export async function startMockRhino(opts: { token?: string; port?: number; dir?: string } = {}): Promise<MockRhino> {
  const token = opts.token ?? "mock-token-1234";
  const calls: MockRhino["calls"] = [];
  const sockets = new Set<net.Socket>();
  const server = net.createServer((sock) => {
    sockets.add(sock);
    sock.on("close", () => sockets.delete(sock));
    sock.setEncoding("utf8");
    let buf = "";
    sock.on("data", async (chunk: string) => {
      buf += chunk;
      let nl: number;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl);
        buf = buf.slice(nl + 1);
        if (!line.trim()) continue;
        let req: { id?: number; token?: string; method?: string; params?: Record<string, unknown> };
        try {
          req = JSON.parse(line);
        } catch {
          continue;
        }
        if (req.token !== token) {
          sock.write(JSON.stringify({ id: req.id, ok: false, error: { code: "unauthorized", message: "bad token" } }) + "\n");
          sock.end();
          return;
        }
        const method = req.method ?? "";
        const params = req.params ?? {};
        calls.push({ method, params });
        const h = handlers[method];
        let reply;
        if (!h) reply = { id: req.id, ok: false, error: { code: "unknown_method", message: method } };
        else {
          try {
            reply = { id: req.id, ok: true, result: await h(params) };
          } catch (e) {
            const err = e as MockError;
            reply = { id: req.id, ok: false, error: { code: err.code ?? "internal", message: err.message } };
          }
        }
        if (!sock.destroyed) sock.write(JSON.stringify(reply) + "\n");
      }
    });
    sock.on("error", () => {});
  });
  await new Promise<void>((resolve) => server.listen(opts.port ?? 0, "127.0.0.1", resolve));
  const port = (server.address() as net.AddressInfo).port;
  const dir = opts.dir ?? fs.mkdtempSync(path.join(os.tmpdir(), "orm-"));
  const sessionFile = path.join(dir, "session.json");
  fs.writeFileSync(
    sessionFile,
    JSON.stringify({ port, token, pid: process.pid, rhino_version: "7.38.0", plugin_version: "0.1.0-mock", protocol: 1, started_at: new Date().toISOString() }),
  );
  return {
    port,
    token,
    sessionFile,
    calls,
    close: () =>
      new Promise<void>((resolve) => {
        for (const s of sockets) s.destroy();
        server.close(() => resolve());
      }),
  };
}

// Run standalone: `npx tsx test/mock-rhino.ts` prints the session file path.
if (import.meta.url === `file://${process.argv[1]}`) {
  startMockRhino({ port: 47820 }).then((m) => console.log(`mock Rhino on ${m.port}, session file ${m.sessionFile}`));
}
