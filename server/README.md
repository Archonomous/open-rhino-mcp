# open-rhino-mcp (MCP server)

The Node half of Open Rhino MCP. It speaks MCP over stdio to your agent and forwards
calls to the Open Rhino MCP plug-in running inside Rhino 7 (see `../plugin` and
`../docs/PROTOCOL.md`).

```
Agent ⇄ MCP (stdio) ⇄ open-rhino-mcp (Node) ⇄ TCP 127.0.0.1 ⇄ Rhino plug-in ⇄ RhinoCommon
```

Requires Node 18+ (Claude Desktop bundles its own Node for the `.mcpb` extension).
The plug-in must be loaded in Rhino; it writes a session file with its port and token,
which this server reads automatically:

- Windows: `%APPDATA%\OpenRhinoMCP\session.json`
- macOS: `~/Library/Application Support/OpenRhinoMCP/session.json`

Overrides: `OPEN_RHINO_MCP_HOST`, `OPEN_RHINO_MCP_PORT`, `OPEN_RHINO_MCP_TOKEN`,
`OPEN_RHINO_MCP_SESSION_FILE`.

## Connect an agent

### Claude Desktop
Double-click `open-rhino-mcp.mcpb` (from a release, or `npm run pack:mcpb` → `out/`)
and click Install. Or add it by hand to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "rhino": {
      "command": "node",
      "args": ["C:\\Users\\you\\Documents\\GitHub\\open-rhino-mcp\\server\\dist\\index.js"]
    }
  }
}
```

### Claude Code
```bash
claude mcp add rhino -- npx -y open-rhino-mcp          # once published to npm
claude mcp add rhino -- node /path/to/open-rhino-mcp/server/dist/index.js
```

### Cursor (`.cursor/mcp.json`) / VS Code (`.vscode/mcp.json`)
```json
{ "mcpServers": { "rhino": { "command": "npx", "args": ["-y", "open-rhino-mcp"] } } }
```
VS Code uses `"servers"` instead of `"mcpServers"` and accepts `"type": "stdio"`.

### Codex (`~/.codex/config.toml`)
```toml
[mcp_servers.rhino]
command = "npx"
args = ["-y", "open-rhino-mcp"]
```

## Tools

38 tools, all prefixed `rhino_`:

| Area | Tools |
|---|---|
| Session & docs | `rhino_status`, `rhino_document_info`, `rhino_guide`, `rhino_command_help`, `rhino_list_commands`, `rhino_python_help`, `rhino_api_lookup` |
| Commands & scripts | `rhino_run_command`, `rhino_command_history`, `rhino_run_python`, `rhino_undo`, `rhino_redo`, `rhino_file` |
| Layers | `rhino_list_layers`, `rhino_upsert_layer`, `rhino_delete_layer` |
| Objects & tags | `rhino_query_objects`, `rhino_get_objects`, `rhino_select_objects`, `rhino_delete_objects`, `rhino_set_attributes`, `rhino_transform_objects`, `rhino_get_user_text`, `rhino_set_user_text` |
| Geometry | `rhino_create_geometry`, `rhino_geometry_op`, `rhino_boolean`, `rhino_analyze` |
| Blocks | `rhino_list_blocks`, `rhino_create_block`, `rhino_insert_blocks` |
| Annotation | `rhino_create_annotations`, `rhino_edit_annotations` |
| Views & output | `rhino_list_views`, `rhino_set_view`, `rhino_capture_view`, `rhino_create_layout`, `rhino_export_pdf` |

Also: guide topics as resources (`rhino7://guide/<topic>`) and two prompts
(`draft_check`, `schedule_from_tags`).

## Develop

```bash
npm install
npm run build        # typecheck + bundle to dist/index.js
npm test             # unit + end-to-end tests against a mock Rhino (test/mock-rhino.ts)
npm run pack:mcpb    # out/open-rhino-mcp.mcpb for Claude Desktop
```

Try the server without Rhino: `npx tsx test/mock-rhino.ts` starts a fake plug-in on
port 47820 and prints its session file; point `OPEN_RHINO_MCP_SESSION_FILE` at it and run
`npx @modelcontextprotocol/inspector node dist/index.js`.

Source layout: `src/bridge.ts` (TCP client, discovery, timeouts), `src/tools.ts` (tool
definitions → bridge methods), `src/schemas.ts` (shared zod shapes), `src/docs.ts`
(Rhino 7 help pages), `src/guide.ts` (best-practice topics), `src/index.ts` (server).

License: AGPL-3.0-only.
