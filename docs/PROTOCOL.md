# Open Rhino MCP — bridge protocol (v1)

The Rhino plugin (C#, inside Rhino) and the MCP server (Node, outside Rhino) talk over a
local TCP socket. The MCP server is the only thing agents talk to; this protocol is internal
but stable, so other front-ends can be written against it.

```
Agent (Claude, Cursor, Codex…) ⇄ MCP (stdio) ⇄ Node server ⇄ TCP 127.0.0.1 ⇄ Rhino plugin ⇄ RhinoCommon
```

## Transport

- TCP, bound to `127.0.0.1` only. Default port **47820**; if taken, the plugin falls back to an
  OS-assigned port.
- Framing: **newline-delimited JSON** (one UTF-8 JSON object per line, `\n` terminated). No
  embedded raw newlines (JSON string escapes are fine).
- One connection may carry many requests. The plug-in handles them concurrently and responses may
  arrive out of order: match them by `id`. Work on Rhino's UI thread is still serialized, and
  mutating methods fail with `rhino_busy` while a Rhino command is active.
- All handler work runs on Rhino's UI thread. Default per-request timeout: 120 s
  (`params.timeout_ms` may override, max 600 000).

## Discovery & auth

On start the plugin writes a discovery file and deletes it on stop:

| OS      | Path |
|---------|------|
| Windows | `%APPDATA%\OpenRhinoMCP\session.json` |
| macOS   | `~/Library/Application Support/OpenRhinoMCP/session.json` |

```json
{ "port": 47820, "token": "hex-32-bytes", "pid": 1234, "rhino_version": "7.38.24338.17001",
  "plugin_version": "0.1.0", "protocol": 1, "started_at": "2026-10-22T19:30:00Z" }
```

Environment overrides for the Node server: `OPEN_RHINO_MCP_HOST`, `OPEN_RHINO_MCP_PORT`,
`OPEN_RHINO_MCP_TOKEN`, `OPEN_RHINO_MCP_SESSION_FILE`.

Every request carries the token. A request with a wrong or missing token gets
`{"ok":false,"error":{"code":"unauthorized"}}` and the connection is closed.

## Envelope

Request:
```json
{ "id": 7, "token": "…", "method": "objects.query", "params": { … } }
```
Response:
```json
{ "id": 7, "ok": true,  "result": { … } }
{ "id": 7, "ok": false, "error": { "code": "invalid_params", "message": "…", "details": { … } } }
```

Error codes: `unauthorized`, `unknown_method`, `invalid_params`, `not_found`,
`rhino_busy` (a command is already running), `timeout`, `operation_failed`, `internal`.

## Shared value shapes

- **Point**: `[x, y, z]`; `[x, y]` means z = 0. **Vector**: same.
- **Id**: GUID string `"0b6f…"`.
- **Color**: `"#RRGGBB"` (input also accepts `[r,g,b]`). Output always `"#RRGGBB"`.
- **Plane** (input): `"world_xy" | "world_yz" | "world_zx" | "cplane"` or
  `{ "origin": P, "x_axis": V, "y_axis": V }` or `{ "origin": P, "normal": V }`. Default `world_xy`.
  Output: `{ "origin": P, "x_axis": V, "y_axis": V, "normal": V }`.
- **BBox**: `{ "min": P, "max": P }`.
- **Attributes** (input, all optional):
  ```json
  { "layer": "E-POWR::Receptacles", "name": "R-11a", "color": "#FF0000",
    "color_source": "layer|object|material|parent", "linetype": "Dashed",
    "print_width": 0.25, "print_color": "#000000", "visible": true, "locked": false,
    "user_text": { "circuit": "11", "VA": "180" }, "groups": ["Kitchen"],
    "layout": "E1" }
  ```
  A `layer` given by full path (`::` separated) is created if missing. `layout` puts the object in
  that layout's page space (title blocks, sheet notes); `"model"` puts it back in model space.
  ObjectSummary includes `"layout": name` for page-space objects.
- **ObjectSummary** (output):
  ```json
  { "id": "…", "type": "curve|brep|extrusion|mesh|subd|point|pointcloud|text|leader|dimension|textdot|hatch|block_instance|detail|light|other",
    "geometry_type": "PolylineCurve", "layer": "A::B", "name": "", "color": "#…",
    "color_source": "layer", "visible": true, "locked": false, "selected": false,
    "bbox": BBox, "user_text": { … }, "groups": ["…"],
    "block": "Receptacle",          // block_instance only
    "text": "…",                    // text, leader, textdot, dimension (displayed text)
    "length": 12.5, "closed": true  // curves only
  }
  ```
- **Change set** (returned by every mutating method):
  `{ "created_ids": [], "deleted_ids": [], "modified_ids": [] }` collected from document events
  during the call.

Every mutating method runs inside one undo record named `Open Rhino MCP: <method>` and redraws
views once at the end.

## Methods

### system
| method | params | result |
|---|---|---|
| `system.hello` | — | `{ protocol, plugin_version, rhino_version, platform, methods: [] }` |
| `system.status` | — | `{ rhino_version, doc_name, doc_path, modified, units, abs_tolerance, angle_tolerance_deg, object_count, layer_count, current_layer, active_view, command_running }` |

### commands
| method | params | result |
|---|---|---|
| `commands.list` | `{ filter?: string, limit?: 2000 }` | `{ count, commands: [string] }` (English names, loaded commands incl. plug-ins) |
| `commands.run` | `{ macro: string, echo?: false, select_ids?: [Id], capture_history?: true, max_wait_ms?: 30000 }` | `{ success: bool, history: string, cancelled?: true, message?, …ChangeSet }` — runs `RhinoApp.RunScript`. `select_ids` replaces the selection first. `history` is the command-line text added during the run. A command still running after `max_wait_ms` (usually stuck at an unanswered prompt) is cancelled with Esc and reported as `cancelled`. Fails with `rhino_busy` if a command is active. |
| `commands.cancel` | — | `{ was_running, cancel_sent }` — presses Esc (and sends `!`) to cancel the running command. Allowed while a command runs. |
| `commands.history` | `{ lines?: 50 }` | `{ text }` |

### python (IronPython 2.7, Rhino's built-in engine)
| method | params | result |
|---|---|---|
| `python.run` | `{ code: string, inputs?: {name: json} }` | `{ ok, output, result, error?, traceback?, …ChangeSet }` — `print` output is captured. If the script assigns a variable named `result`, it is returned (JSON-converted; Rhino types become strings / ObjectSummary for ids where possible). `inputs` are set as globals. `rs`, `sc`, `Rhino`, `System` are pre-imported. |
| `python.help` | `{ name: string }` | `{ name, kind, signature?, doc, members?: [string] }` — e.g. `"rs"`, `"rs.AddLine"`, `"Rhino.Geometry.Brep"`. |

### api
| method | params | result |
|---|---|---|
| `api.reflect` | `{ query: string, member?: string, limit?: 25 }` | `{ types: [{ full_name, kind, base, members?: [string] }] }` — searches loaded RhinoCommon (and Rhino.UI, Eto) public types whose name contains `query` (case-insensitive). If exactly one type matches, or `member` is given, member signatures are included (filtered by `member` substring). |

### doc
| method | params | result |
|---|---|---|
| `doc.info` | — | status fields + `{ layers: n, layouts: [name], named_views: [name], blocks: n, dimstyles: [name], current_dimstyle, linetypes: [name], materials: n, groups: [name], document_user_text: {…}, notes }` |
| `doc.file` | `{ action: "new"|"open"|"save"|"save_as"|"import"|"export_selected"|"save_copy", path?, select_ids? }` | `{ success, path, …ChangeSet }` — implemented with scripted commands (`-_Open`, `-_SaveAs`, `-_Import`, `-_Export`). `new` / `open` discard nothing silently: they fail with `operation_failed` if the doc is modified unless `params.discard_changes: true`. |
| `doc.undo` | `{ steps?: 1 }` | `{ success }` |
| `doc.redo` | `{ steps?: 1 }` | `{ success }` |

### layers
| method | params | result |
|---|---|---|
| `layers.list` | `{ include_counts?: true }` | `{ current, layers: [{ path, name, id, index, parent, color, visible, locked, linetype, print_width, print_color, object_count }] }` |
| `layers.upsert` | `{ path, color?, visible?, locked?, linetype?, print_width?, print_color?, make_current?, rename_to? }` | `{ layer: Layer, created: bool }` |
| `layers.delete` | `{ path, delete_objects?: false }` | `{ success }` — fails if layer has objects and `delete_objects` is false, or if it is current. |

### objects
| method | params | result |
|---|---|---|
| `objects.query` | filters (all optional, AND-ed): `{ ids?, types?: [type], layer?: path, include_sublayers?: true, name?: glob, user_text?: {key: value or "*"}, block?: name, selected_only?, include_hidden?: false, include_locked?: true, bbox?: BBox (intersects), text_contains?: string, limit?: 200, offset?: 0 }` | `{ total, count, offset, objects: [ObjectSummary] }` |
| `objects.get` | `{ ids: [Id], detail?: "summary"|"full" }` | `{ objects: [ObjectSummary + geometry] }` — `full` adds geometry data: points (point), start/end (line), vertices (polyline ≤ 2000), center/radius/plane (circle/arc), degree/control points/knots count (nurbs, points ≤ 2000), faces/edges/is_solid/area/volume (brep/extrusion), vertex/face counts (mesh), plane/height/text/rich_text/dimstyle/justification (text), measured value (dimension), insertion point/xform/definition (block instance). |
| `objects.select` | `{ ids?: [Id], mode?: "replace"|"add"|"remove"|"clear" }` | `{ selected_count }` |
| `objects.delete` | `{ ids: [Id] }` | ChangeSet |
| `objects.set_attributes` | `{ ids: [Id], attributes: Attributes }` | ChangeSet (`user_text` here merges; value `null` deletes a key) |
| `objects.transform` | `{ ids, op: "move"|"rotate"|"scale"|"mirror"|"matrix", copy?: false, translation?: V, angle_deg?, axis?: V (default Z), center?: P, factor?: number or [x,y,z], plane?: Plane (mirror plane; default world_yz through center), matrix?: [[4x4]] }` | ChangeSet |

### usertext
| method | params | result |
|---|---|---|
| `usertext.get` | `{ ids?: [Id], document?: false, keys?: [string] }` | `{ objects: [{ id, user_text }], document?: {…} }` |
| `usertext.set` | `{ ids?: [Id], document?: false, values: {key: string or null}, mode?: "merge"|"replace" }` | ChangeSet |

### geometry
| method | params | result |
|---|---|---|
| `geometry.create` | `{ items: [Item] }` (≤ 500) | `{ ids: [Id or null], errors: [{index, message}] }` |
| `geometry.op` | `{ op, ids?: [Id], delete_input?: false, attributes?: Attributes, … }` | `{ ids: [Id], …ChangeSet }` |
| `geometry.boolean` | `{ op: "union"|"difference"|"intersection"|"split", a_ids: [Id], b_ids?: [Id], delete_input?: true, attributes? }` | `{ ids, …ChangeSet }` — closed breps/extrusions, or closed planar curves (2D region booleans). |
| `geometry.analyze` | `{ op: "measure"|"intersect"|"closest_point"|"distance", ids?, point?: P, point_b?: P }` | measure → `{ items: [{ id, length?, area?, volume?, centroid?, bbox, is_closed?, is_solid? }] }`; intersect (2 ids) → `{ points: [P], curves: [Id-less polylines as point lists], overlaps }`; closest_point (1 id + point) → `{ point, distance }`; distance (2 points) → `{ distance }` |

`Item` for `geometry.create` — every item may carry `attributes`:
```
{ type: "point", point }
{ type: "line", start, end }
{ type: "polyline", points, closed? }
{ type: "rectangle", plane?, corner?: P (default plane origin), width, height }
{ type: "circle", plane?, center?, radius }
{ type: "arc", start, mid, end }            | { type: "arc", plane?, center, radius, start_angle_deg, end_angle_deg }
{ type: "ellipse", plane?, center?, radius_x, radius_y }
{ type: "polygon", plane?, center?, radius, sides, circumscribed?: false }
{ type: "curve", points, degree?: 3, interpolate?: true, closed?: false }
{ type: "box", corner_a, corner_b }          | { type: "box", plane?, corner?, size: [x,y,z] }
{ type: "sphere", center, radius }
{ type: "cylinder", plane?, center?, radius, height, cap?: true }
{ type: "cone", plane?, center?, radius, height, cap?: true }
{ type: "plane_surface", plane?, corner?, width, height }
{ type: "text", … } → use annotations.create instead (rejected here)
```

`geometry.op` ops:
```
extrude      { ids (curves), direction: V | height (along curve plane normal / Z), cap?: true }
loft         { ids (curves, in order), closed?: false, loft_type?: "normal"|"loose"|"tight"|"straight" }
revolve      { ids (curves), axis_start: P, axis_end: P, start_deg?: 0, end_deg?: 360 }
sweep1       { rail_id, profile_ids, closed?: false }
sweep2       { rail_ids: [Id, Id], profile_ids, closed?: false }
pipe         { ids (curves), radius, cap?: "none"|"flat"|"round" }
offset_curve { ids, distance, plane?, corner?: "sharp"|"round"|"smooth"|"chamfer" }
join         { ids }   (curves join with curves, breps with breps)
explode      { ids }   (curves → segments, polysurfaces → faces; blocks: use commands.run "_Explode")
cap          { ids }   (cap planar holes)
fillet_curves{ ids: [a, b], radius }
rebuild_curve{ ids, point_count, degree?: 3 }
project      { ids (curves), target_ids (breps), direction?: [0,0,-1] }
split_curve  { ids, cutter_ids }
trim_curve   { ids, cutter_ids, keep_point: P }     (keeps the piece nearest keep_point)
```

### blocks
| method | params | result |
|---|---|---|
| `blocks.list` | `{ include_counts?: true }` | `{ blocks: [{ name, id, index, description, object_count, instance_count, is_linked, path? }] }` |
| `blocks.create` | `{ name, ids, base_point: P, description?, delete_input?: true, replace_with_instance?: true }` | `{ definition, instance_id?, …ChangeSet }` |
| `blocks.insert` | `{ items: [{ name, point, rotation_deg?: 0, scale?: 1 or [x,y,z], attributes? }] }` | `{ ids, errors }` |

### annotations
| method | params | result |
|---|---|---|
| `annotations.create` | `{ items: [Ann] }` | `{ ids, errors }` |
| `annotations.edit` | `{ ids, text?, find?, replace?, height?, dimstyle? }` | ChangeSet — editing rebuilds plain text (rich formatting on edited objects is lost). |

`Ann` items (each may carry `attributes`):
```
{ type: "text", text, point, height?, plane?, dimstyle?, justification?: "left"|"center"|"right"|"bottom_left"|"middle_center"|…, font?, rotation_deg?, wrap_width? }
{ type: "leader", text, points: [P…] (arrow tip first), plane?, dimstyle? }
{ type: "dimension", kind?: "aligned"|"horizontal"|"vertical", start, end, line_point, plane?, dimstyle? }
{ type: "dot", text, point, height?: 14 }
{ type: "hatch", boundary_ids: [Id], pattern?: "Solid", scale?: 1, rotation_deg?: 0 }
```
Standard hatch patterns (Solid, Hatch1–3, HatchDash, Grid, Grid60, Plus, Squares) are added to the
document on first use if missing.

### views & output
| method | params | result |
|---|---|---|
| `views.list` | — | `{ active, views: [{ name, kind: "model"|"layout", projection?, display_mode?, page_number?, width?, height?, details?: [{ id, name, scale_text?, projection, locked }] }], named_views: [name], display_modes: [name] }` |
| `views.set` | `{ view?: name, action: "activate"|"zoom_extents"|"zoom_selected"|"zoom_ids"|"projection"|"named_view"|"display_mode"|"save_named_view", ids?, projection?: "top"|"bottom"|"left"|"right"|"front"|"back"|"perspective"|"two_point_perspective", named_view?, display_mode?, all_views?: false }` | `{ success }` |
| `views.capture` | `{ view?: name, width?: 1600, height?: 1000, display_mode?, transparent?: false, zoom_ids?: [Id], grid?: false, axes?: false }` | `{ view, width, height, png_base64 }` |
| `layouts.create` | `{ name, width, height (page units), details?: [{ corner_a: [x,y], corner_b: [x,y], projection?: "top"…, scale?: { model: 1.5, model_units: "feet", page: 1, page_units: "inches" }, zoom_ids?: [Id], lock?: true, name? }] }` | `{ view, details: [Id] }` |
| `export.pdf` | `{ path, layouts?: [name] (default all layouts in page order), view?: name (model view instead), dpi?: 300, color?: "display"|"print"|"black_white", width?: 11, height?: 8.5 (inches, model view only) }` | `{ path, pages }` |
