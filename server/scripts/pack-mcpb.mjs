// Stage dist + manifest into out/stage, fill in the tool list, then `mcpb pack`.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const out = path.join(root, "out");
const stage = path.join(out, "stage");
fs.rmSync(stage, { recursive: true, force: true });
fs.mkdirSync(path.join(stage, "dist"), { recursive: true });

fs.copyFileSync(path.join(root, "dist", "index.js"), path.join(stage, "dist", "index.js"));
for (const f of ["README.md"]) if (fs.existsSync(path.join(root, f))) fs.copyFileSync(path.join(root, f), path.join(stage, f));
const license = path.join(root, "..", "LICENSE");
if (fs.existsSync(license)) fs.copyFileSync(license, path.join(stage, "LICENSE"));
const icon = path.join(root, "mcpb", "icon.png");

const manifest = JSON.parse(fs.readFileSync(path.join(root, "mcpb", "manifest.json"), "utf8"));
const pkg = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
manifest.version = pkg.version;
if (fs.existsSync(icon)) {
  fs.copyFileSync(icon, path.join(stage, "icon.png"));
  manifest.icon = "icon.png";
}

// Ask the built server for its tools so the manifest never drifts from the code.
const list = execFileSync(process.execPath, [path.join(root, "dist", "index.js")], {
  input:
    JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "pack", version: "1" } } }) +
    "\n" +
    JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) +
    "\n" +
    JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" }) +
    "\n" +
    JSON.stringify({ jsonrpc: "2.0", id: 3, method: "prompts/list" }) +
    "\n",
  encoding: "utf8",
  timeout: 15000,
});
const replies = list.trim().split("\n").map((l) => JSON.parse(l));
const tools = replies.find((r) => r.id === 2).result.tools;
const prompts = replies.find((r) => r.id === 3).result.prompts;
manifest.tools = tools.map((t) => ({ name: t.name, description: t.description.split(/(?<=\.)\s/)[0] }));
manifest.prompts = prompts.map((p) => ({
  name: p.name,
  description: p.description,
  arguments: (p.arguments ?? []).map((a) => a.name),
  text: `Run the ${p.name} prompt.`,
}));
fs.writeFileSync(path.join(stage, "manifest.json"), JSON.stringify(manifest, null, 2));

const mcpb = path.join(root, "node_modules", ".bin", process.platform === "win32" ? "mcpb.cmd" : "mcpb");
const shell = process.platform === "win32";
execFileSync(mcpb, ["validate", path.join(stage, "manifest.json")], { stdio: "inherit", shell });
const target = path.join(out, "open-rhino-mcp.mcpb");
fs.rmSync(target, { force: true });
execFileSync(mcpb, ["pack", stage, target], { stdio: "inherit", shell });
console.log(`wrote ${path.relative(root, target)} (${tools.length} tools)`);
