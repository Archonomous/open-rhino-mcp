# Open Rhino MCP

Let AI agents work inside **Rhino 7**: Claude, Cursor, Codex, VS Code and any other
[Model Context Protocol](https://modelcontextprotocol.io) client.

Open Rhino MCP is built for production drawing work, not just making boxes. Agents can query a
model by layer, block and **user text**, read and write tags, draft annotations, build layouts,
export PDFs and check their own work with viewport captures. When a typed tool doesn't cover a
step, they can run any Rhino command or IronPython script.

> Status: **0.1.0, Windows first.** macOS (Rhino 7 for Mac) and Rhino 8 support are planned.

## How it works

```
Agent ⇄ MCP (stdio) ⇄ open-rhino-mcp (Node) ⇄ TCP 127.0.0.1 ⇄ Rhino plug-in ⇄ RhinoCommon
```

- **`plugin/`**: a RhinoCommon plug-in (`OpenRhinoMcp.rhp`, .NET Framework 4.8). It loads with
  Rhino, listens on `127.0.0.1` only, and runs every request on Rhino's UI thread inside one
  undo step.
- **`server/`**: the MCP server (TypeScript). It ships as a one-click Claude Desktop extension
  (`.mcpb`) and as an `npx` command for every other client.
- **`docs/PROTOCOL.md`**: the bridge protocol between the two halves.

The plug-in writes a session file (port and a random token) to
`%APPDATA%\OpenRhinoMCP\session.json`. The server reads it, so nothing needs configuring and
other local programs can't drive Rhino without the token.

## Install (Windows, Rhino 7)

1. **Plug-in.** Get `OpenRhinoMcp.rhp` and `Newtonsoft.Json.dll` from the latest CI run
   (artifact `open-rhino-mcp-plugin`) or a release. Put both in one folder, then drag the `.rhp`
   onto a Rhino window (or *Tools → Options → Plug-ins → Install…*). Run `OpenRhinoMcpStatus`.
   It should say it's listening on `127.0.0.1:47820`.
2. **Agent.**
   - *Claude Desktop:* double-click `open-rhino-mcp.mcpb` (artifact `open-rhino-mcp-mcpb`) and
     click Install.
   - *Claude Code / Cursor / VS Code / Codex:* see [server/README.md](server/README.md).
3. Ask your agent "what's in my Rhino file?"

### Rhino commands

| Command | What it does |
|---|---|
| `OpenRhinoMcpStatus` | Port, connected clients, session file |
| `OpenRhinoMcpStart` / `OpenRhinoMcpStop` | Start or stop the bridge |
| `OpenRhinoMcpSettings` | Auto-start with Rhino (default on) and preferred port |

## Tools (38)

| Area | Tools |
|---|---|
| Orientation & knowledge | `rhino_status`, `rhino_document_info`, `rhino_guide`, `rhino_list_commands`, `rhino_command_help` (official Rhino 7 docs), `rhino_python_help` (live rhinoscriptsyntax docstrings), `rhino_api_lookup` (live RhinoCommon reflection) |
| Escape hatches | `rhino_run_command` (any Rhino command or macro), `rhino_run_python` (IronPython 2.7), `rhino_command_history`, `rhino_undo`, `rhino_redo` |
| Files | `rhino_file` (new, open, save, save as, save copy, import, export selected) |
| Layers | `rhino_list_layers`, `rhino_upsert_layer`, `rhino_delete_layer` |
| Objects & tags | `rhino_query_objects` (by type, layer, name, user text, block, bbox, text), `rhino_get_objects`, `rhino_select_objects`, `rhino_delete_objects`, `rhino_set_attributes`, `rhino_transform_objects`, `rhino_get_user_text`, `rhino_set_user_text` |
| Geometry | `rhino_create_geometry`, `rhino_geometry_op` (extrude, loft, revolve, sweeps, pipe, offset, join, explode, cap, fillet, rebuild, project, split, trim), `rhino_boolean` (solids and 2D regions), `rhino_analyze` |
| Blocks | `rhino_list_blocks`, `rhino_create_block`, `rhino_insert_blocks` |
| Drafting | `rhino_create_annotations` (text, leaders, dimensions, dots), `rhino_edit_annotations`, `rhino_list_views`, `rhino_set_view`, `rhino_capture_view`, `rhino_create_layout`, `rhino_export_pdf` |

### Tag-driven drawings

Store the data on the objects, not in their names. If each receptacle block carries user text
like `circuit=11`, `VA=180`, `protection=DF`, an agent can query by tag, build the panel schedule
and load calculation from the model, check code spacing against the geometry, and write the
results back onto the sheet.

## Building

```bash
dotnet build plugin/OpenRhinoMcp.csproj -c Release   # → plugin/bin/Release/OpenRhinoMcp.rhp
cd server && npm ci && npm test && npm run pack:mcpb  # → server/out/open-rhino-mcp.mcpb
```

The plug-in compiles against the RhinoCommon 7.13 NuGet package, so it builds on any OS (CI uses
Windows). It loads on Rhino 7.13 and later.

## Security

The bridge only listens on localhost and needs the session token. `rhino_run_python` and
`rhino_run_command` can do anything you can do in Rhino, so connect only agents you trust.
Every mutating call is one undo step.

## License

[AGPL-3.0](LICENSE). Rhino and Rhinoceros are trademarks of Robert McNeel & Associates. This
project isn't affiliated with or endorsed by McNeel.
