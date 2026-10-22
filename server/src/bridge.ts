// TCP client for the Open Rhino MCP plug-in (see docs/PROTOCOL.md).
import net from "node:net";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export const NOT_CONNECTED_MESSAGE =
  "Rhino isn't connected. Open Rhino 7, make sure the Open Rhino MCP plug-in is loaded (type OpenRhinoMcpStatus), then try again.";

export const DEFAULT_PORT = 47820;
export const DEFAULT_TIMEOUT_MS = 120_000;
export const MAX_TIMEOUT_MS = 600_000;

export interface Endpoint {
  host: string;
  port: number;
  token: string;
  source: string;
}

export interface SessionFile {
  port?: number;
  token?: string;
  pid?: number;
  rhino_version?: string;
  plugin_version?: string;
  protocol?: number;
  started_at?: string;
}

/** Error raised for anything that should surface to the agent as a tool error. */
export class BridgeError extends Error {
  constructor(
    public code: string,
    message: string,
    public details?: unknown,
  ) {
    super(message);
    this.name = "BridgeError";
  }
}

export function defaultSessionFile(env: NodeJS.ProcessEnv = process.env): string {
  if (process.platform === "win32") {
    const appData = env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
    return path.join(appData, "OpenRhinoMCP", "session.json");
  }
  if (process.platform === "darwin") {
    return path.join(os.homedir(), "Library", "Application Support", "OpenRhinoMCP", "session.json");
  }
  const xdg = env.XDG_CONFIG_HOME || path.join(os.homedir(), ".config");
  return path.join(xdg, "OpenRhinoMCP", "session.json");
}

/** Work out where the plug-in is listening. Re-run on every reconnect: Rhino restarts change port + token. */
export function resolveEndpoint(rawEnv: NodeJS.ProcessEnv = process.env): Endpoint {
  // Ignore empty values and unexpanded placeholders such as "${user_config.port}" from extension hosts.
  const env: NodeJS.ProcessEnv = {};
  for (const [k, v] of Object.entries(rawEnv)) if (v && !v.startsWith("${")) env[k] = v;
  const host = env.OPEN_RHINO_MCP_HOST || "127.0.0.1";
  const sessionPath = env.OPEN_RHINO_MCP_SESSION_FILE || defaultSessionFile(env);
  let session: SessionFile = {};
  let sessionRead = false;
  try {
    session = JSON.parse(fs.readFileSync(sessionPath, "utf8").replace(/^﻿/, ""));
    sessionRead = true;
  } catch {
    /* missing or unreadable: fall back to env */
  }
  const envPort = env.OPEN_RHINO_MCP_PORT ? Number(env.OPEN_RHINO_MCP_PORT) : undefined;
  const port = envPort || session.port || DEFAULT_PORT;
  const token = env.OPEN_RHINO_MCP_TOKEN || session.token || "";
  if (!token) {
    throw new BridgeError(
      "not_connected",
      NOT_CONNECTED_MESSAGE + ` (No session file at ${sessionPath}.)`,
    );
  }
  return { host, port, token, source: sessionRead ? sessionPath : "environment" };
}

interface Pending {
  resolve: (v: unknown) => void;
  reject: (e: Error) => void;
  timer: NodeJS.Timeout;
  method: string;
}

const ERROR_TEXT: Record<string, string> = {
  unauthorized:
    "Rhino rejected the connection token. Rhino may have restarted; the server re-reads the session file on the next call — try again.",
  unknown_method: "This version of the Rhino plug-in doesn't support that operation. Update the Open Rhino MCP plug-in.",
  invalid_params: "Invalid parameters",
  not_found: "Not found",
  rhino_busy: "Rhino is busy running a command. Finish or cancel it in Rhino (press Esc), then try again.",
  timeout: "Rhino didn't finish in time",
  operation_failed: "Rhino couldn't complete the operation",
  internal: "The Rhino plug-in hit an internal error",
};

export function describeError(code: string, message?: string): string {
  const base = ERROR_TEXT[code];
  if (!base) return message || code;
  if (!message || message === base) return base;
  if (code === "unauthorized" || code === "rhino_busy" || code === "unknown_method") return `${base} (${message})`;
  return `${base}: ${message}`;
}

export interface BridgeOptions {
  env?: NodeJS.ProcessEnv;
  connectTimeoutMs?: number;
}

export class RhinoBridge {
  private socket: net.Socket | null = null;
  private connecting: Promise<net.Socket> | null = null;
  private endpoint: Endpoint | null = null;
  private buffer = "";
  private nextId = 1;
  private pending = new Map<number, Pending>();
  private env: NodeJS.ProcessEnv;
  private connectTimeoutMs: number;

  constructor(opts: BridgeOptions = {}) {
    this.env = opts.env ?? process.env;
    this.connectTimeoutMs = opts.connectTimeoutMs ?? 3000;
  }

  /** Send one request. Reconnects once (with a fresh discovery read) if the socket is dead. */
  async call<T = unknown>(method: string, params: Record<string, unknown> = {}, timeoutMs?: number): Promise<T> {
    const timeout = Math.min(Math.max(timeoutMs ?? DEFAULT_TIMEOUT_MS, 1), MAX_TIMEOUT_MS);
    let attempt = 0;
    for (;;) {
      try {
        const sock = await this.ensureConnected();
        return (await this.send(sock, method, params, timeout)) as T;
      } catch (e) {
        const err = e as BridgeError;
        const retryable = err.code === "not_connected" || err.code === "connection_lost" || err.code === "unauthorized";
        if (retryable && attempt === 0) {
          attempt++;
          this.reset();
          continue;
        }
        if (err.code === "connection_lost" || err.code === "not_connected") {
          throw new BridgeError("not_connected", err.code === "not_connected" ? err.message : NOT_CONNECTED_MESSAGE);
        }
        throw err;
      }
    }
  }

  close(): void {
    this.reset();
  }

  private reset(): void {
    if (this.socket) {
      this.socket.removeAllListeners();
      this.socket.destroy();
    }
    this.socket = null;
    this.connecting = null;
    this.endpoint = null;
    this.buffer = "";
    this.failAll(new BridgeError("connection_lost", NOT_CONNECTED_MESSAGE));
  }

  private failAll(err: Error): void {
    for (const [, p] of this.pending) {
      clearTimeout(p.timer);
      p.reject(err);
    }
    this.pending.clear();
  }

  private ensureConnected(): Promise<net.Socket> {
    if (this.socket && !this.socket.destroyed) return Promise.resolve(this.socket);
    if (this.connecting) return this.connecting;
    let endpoint: Endpoint;
    try {
      endpoint = resolveEndpoint(this.env);
    } catch (e) {
      return Promise.reject(e);
    }
    this.connecting = new Promise<net.Socket>((resolve, reject) => {
      const sock = net.createConnection({ host: endpoint.host, port: endpoint.port });
      sock.setNoDelay(true);
      const timer = setTimeout(() => {
        sock.destroy();
        reject(new BridgeError("not_connected", NOT_CONNECTED_MESSAGE));
      }, this.connectTimeoutMs);
      sock.once("connect", () => {
        clearTimeout(timer);
        sock.setEncoding("utf8");
        sock.on("data", (chunk: string) => this.onData(chunk));
        sock.on("close", () => {
          if (this.socket === sock) {
            this.socket = null;
            this.connecting = null;
            this.buffer = "";
            this.failAll(new BridgeError("connection_lost", NOT_CONNECTED_MESSAGE));
          }
        });
        sock.on("error", () => {
          /* close follows */
        });
        this.socket = sock;
        this.endpoint = endpoint;
        resolve(sock);
      });
      sock.once("error", () => {
        clearTimeout(timer);
        reject(new BridgeError("not_connected", NOT_CONNECTED_MESSAGE));
      });
    }).finally(() => {
      this.connecting = null;
    });
    return this.connecting;
  }

  private send(sock: net.Socket, method: string, params: Record<string, unknown>, timeoutMs: number): Promise<unknown> {
    const id = this.nextId++;
    const token = this.endpoint?.token ?? "";
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(
          new BridgeError(
            "timeout",
            `Rhino didn't answer ${method} within ${Math.round(timeoutMs / 1000)} s. It may still be working, or a dialog may be open in Rhino.`,
          ),
        );
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, method });
      const line = JSON.stringify({ id, token, method, params }) + "\n";
      sock.write(line, "utf8", (err) => {
        if (err) {
          clearTimeout(timer);
          this.pending.delete(id);
          reject(new BridgeError("connection_lost", NOT_CONNECTED_MESSAGE));
        }
      });
    });
  }

  private onData(chunk: string): void {
    this.buffer += chunk;
    let nl: number;
    while ((nl = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, nl).trim();
      this.buffer = this.buffer.slice(nl + 1);
      if (!line) continue;
      let msg: { id?: number; ok?: boolean; result?: unknown; error?: { code?: string; message?: string; details?: unknown } };
      try {
        msg = JSON.parse(line);
      } catch {
        continue;
      }
      // An unauthorized reply may come without our id before the plug-in closes the socket.
      if ((msg.id === undefined || msg.id === null) && msg.ok === false && msg.error?.code === "unauthorized") {
        this.failAll(new BridgeError("unauthorized", describeError("unauthorized", msg.error?.message)));
        continue;
      }
      const p = msg.id !== undefined ? this.pending.get(msg.id) : undefined;
      if (!p) continue;
      this.pending.delete(msg.id as number);
      clearTimeout(p.timer);
      if (msg.ok) p.resolve(msg.result);
      else {
        const code = msg.error?.code || "internal";
        p.reject(new BridgeError(code, describeError(code, msg.error?.message), msg.error?.details));
      }
    }
  }
}
