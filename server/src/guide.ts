// Best-practice notes for agents driving Rhino 7. Served by the rhino_guide tool and as
// rhino7://guide/<topic> resources.

export interface GuideTopic {
  title: string;
  summary: string;
  body: string;
}

export const GUIDE: Record<string, GuideTopic> = {
  overview: {
    title: "Working in Rhino 7 through Open Rhino MCP",
    summary: "Start here: how the tools fit together and the order to reach for them.",
    body: `
Rhino 7 is a NURBS modeler with a single active document, a command line, and a
UI thread that does all document work. Every tool call runs on that thread, so keep
calls focused and batch where the tool allows it.

Order of preference:
1. Typed tools (rhino_query_objects, rhino_create_geometry, rhino_create_annotations,
   rhino_set_user_text, rhino_insert_blocks, rhino_create_layout, rhino_export_pdf…).
   They validate input, return object ids, and are one undo step each.
2. rhino_run_command for anything that is a Rhino command (about 1,000 of them). Use
   rhino_list_commands to find names and rhino_command_help for options, then script it
   with dash commands (see topic "scripting_commands").
3. rhino_run_python (IronPython 2.7 with rhinoscriptsyntax and RhinoCommon) for
   loops, math, and anything the first two can't express. rhino_python_help and
   rhino_api_lookup read the live API docs from the running Rhino.

Before modeling: call rhino_status to learn units and tolerance. Most mistakes come
from assuming millimeters in an inches file, or feet in an inches file.

Checking work: rhino_capture_view returns a picture of any view or layout. Look
before you report success, especially for drafting and layout work.

Undo: every mutating call is a single undo record named "Open Rhino MCP: …";
rhino_undo walks back one call per step.

Tags over names: put meaning on objects with user text (key/value) and layers, then
query by it (rhino_query_objects with user_text). See "layers_attributes".

Never discard unsaved work. If a file operation would lose changes, ask the person first.
`,
  },

  scripting_commands: {
    title: "Scripting Rhino commands",
    summary: "Dash commands, English names, quoting, Enter, and selection in macros.",
    body: `
rhino_run_command passes a macro to RhinoApp.RunScript. Write macros the way a
toolbar button would:

- Prefix commands with "_" so the English name works in any Rhino language:
  "_Line", "_Move", "_Hide".
- Prefix with "-" to suppress dialogs and get command-line options instead:
  "-_Export", "-_SaveAs", "-_Layer", "-_Hatch". Combine as "-_Command".
- Options are given as OptionName=Value tokens or by typing the option name, then
  its value: "-_Hatch _Pattern=Grid _Scale=2 _Enter".
- Points: "0,0,0" (world) or "r5,0" (relative to last point), "w" prefix forces world
  coordinates: "w0,0,0".
- Finish prompts with "_Enter". Cancel with "!" at the start of a macro (cancels any
  running command first): "! _-Layer _Current \\"A::B\\" _Enter".
- Quote paths and names that contain spaces with double quotes:
  -_SaveAs "C:\\Projects\\Casita\\E1.3dm" _Enter
- "_Pause" waits for user input; avoid it in automation (the call would hang).
- Selection: pass select_ids to rhino_run_command and the tool selects exactly those
  objects before the macro runs, so commands that act on the selection
  (e.g. "_Explode", "_Join", "_Group") pick them up. Inside a macro you can also use
  "_SelID <guid>" or "_SelLayer \\"Name\\"", "_SelName", "_SelNone", "_SelAll".
- Prefix a command with "'" to run it transparently inside another command ("'_Zoom").
- "_NoEcho" before a command stops it printing to the history.

The result includes the command-line history produced during the run plus created,
deleted and modified object ids. Read the history: Rhino reports failures there
("Unknown command", "Unable to …") rather than as errors.

Check options before scripting an unfamiliar command: rhino_command_help returns the
official Rhino 7 help page for any command.
`,
  },

  units_tolerance: {
    title: "Units and tolerance",
    summary: "Read them first; never mix units silently.",
    body: `
Each document has a model unit system and an absolute tolerance (plus an angle
tolerance). rhino_status reports both.

- All coordinates you send are in model units. In a feet-based architectural file,
  a 24" counter is 2.0, not 24.
- Typical tolerances: millimeters 0.001–0.01, inches 0.001, feet 0.0001–0.001. Joins,
  booleans, trims and intersections all use the document tolerance; a file with a
  loose tolerance (0.1) produces sloppy geometry that later fails to join or cap.
- Don't change units or tolerance on an existing model without asking: "_Units"
  can scale geometry, and that changes everything downstream.
- Layout (page) space has its own units, usually inches or millimeters; detail scale
  ties the two together (see "drafting").
- Angles in tools are degrees; RhinoCommon uses radians (Rhino.RhinoMath.ToRadians).
- Imported files may arrive in different units; Rhino scales on import unless told
  otherwise. Check bounding boxes after an import.
`,
  },

  layers_attributes: {
    title: "Layers, attributes and user text",
    summary: "Full layer paths, object attributes, and user text as tags.",
    body: `
Layers are addressed by full path with "::" between levels: "E-POWR::Receptacles".
rhino_upsert_layer creates missing parents. Object attributes include layer, name,
color (with color source ByLayer/ByObject/ByParent), linetype, print width, print
color, visibility, lock state, groups, and user text.

Recommended workflow: treat user text as tags.
- Put meaning on objects with key/value user text instead of encoding it in names:
  a receptacle block instance might carry circuit=11, VA=180, protection=DF,
  mount=18in. A light fixture: zone=3, lumens=800, cct=3000.
- Query by tags: rhino_query_objects { user_text: { "circuit": "11" } } or
  { user_text: { "protection": "*" } } for "has the key".
- Count, schedule and calculate from tags, then write results back as text or
  document user text. The schedule then can't drift from the drawing.
- Keep keys short, lowercase or consistent, and documented in document user text
  (e.g. key "schema.circuit" = "panel circuit number").
- User text on a block instance is per-instance; user text on objects inside a block
  definition is shared by every instance. Use instance user text for per-device data.

Layers carry print width and color for drawings: set them per layer and leave
objects ByLayer unless an exception is intentional. Hidden or locked layers hide
objects from most commands; rhino_query_objects can include hidden objects when asked.

Object names are fine as labels but are not unique. Ids (GUIDs) are the only stable
handle within a session; they persist in the .3dm file.
`,
  },

  drafting: {
    title: "Drafting, layouts and PDF output",
    summary: "Model vs page space, details and scale, dimstyles, print widths, hatches, PDF.",
    body: `
Model space holds the building at full size. Layouts (page views) are sheets in paper
units holding detail views that look into model space at a set scale.

- Create sheets with rhino_create_layout: page size in page units (e.g. 36 x 24 in),
  details with corners in page units, a projection (usually "top") and a scale such as
  { model: 1.5, model_units: "feet", page: 1, page_units: "inches" } for 1" = 1'-6".
  Lock details after setting scale so panning doesn't change it.
- Annotation that belongs to the sheet (title block, notes, keynotes) goes on the
  layout. Annotation tied to geometry (tags, dimensions, leaders) usually lives in
  model space on annotation layers.
- Dimension styles (dimstyles) control text height, arrows, units and scale. With
  annotation scaling on (the Rhino 7 default for most templates), a model-space text
  height is a PAPER height: a 3 mm note shows as 3 mm on every sheet, whatever the
  detail scale, and Rhino enlarges it in model views by the dimstyle's model-space
  scale. Typing a real-world size (e.g. 250 mm room label) makes it 250 mm on paper:
  enormous in a 1:50 detail. Use paper heights (2.5–3.5 mm / 3/32–1/8 in) for tags,
  room names and dimensions, check the dimstyle in rhino_document_info, and capture
  the layout to confirm. Keep one dimstyle per drawing scale if scaling is off.
- Put sheet annotation (title block, notes, keynotes) on the layout with
  attributes.layout: "<layout name>"; coordinates are then page units.
- Typed tools (create_geometry, annotations, blocks) always draw in model space unless
  attributes.layout names a sheet. Rhino commands and rs.Add* calls in rhino_run_python
  instead follow the ACTIVE view: with a layout active they draw on that sheet. Activate
  a model view (rhino_set_view) before modelling with commands or scripts.
- Text height on layouts is in page units (0.09–0.125 in is typical for notes).
- Print widths come from layers (or objects); check them with "_Print" preview or a PDF.
  Hidden lines and hatches print at their own widths.
- Hatches: use rhino_create_annotations type "hatch" with boundary_ids and a pattern.
  New files often have no hatch patterns loaded; the tool adds the standard ones
  (Solid, Hatch1–3, HatchDash, Grid, Grid60, Plus, Squares) when first used. Pattern
  scale is in model units: start near 1/50 of the region size and check a capture.
- Export with rhino_export_pdf: all layouts in page order by default; choose color
  "print" to honor print colors, or "black_white" for permit sets.
- After any layout change, capture the layout with rhino_capture_view and inspect it.
`,
  },

  blocks: {
    title: "Blocks",
    summary: "Definitions vs instances, embedded vs linked, counting by tags.",
    body: `
A block definition holds geometry once; block instances place it with a transform.
Use blocks for anything repeated: receptacles, fixtures, switches, furniture, symbols.

- Base point matters: define the insertion point where the symbol attaches (the wall
  face, the box center) so rotations and placements are predictable.
- Embedded definitions live in the file. Linked definitions reference an external
  .3dm and update from it ("_BlockManager" > Update). Don't edit linked definitions in
  place; edit the source file.
- Per-instance data goes in instance user text (circuit, VA, mounting height).
  Shared data (manufacturer, model) can live on the definition's objects or in the
  definition description.
- Count with rhino_list_blocks (instance_count) or by tags with rhino_query_objects
  { block: "Receptacle", user_text: { circuit: "11" } }.
- Nested blocks count separately: a block inside another block is not a top-level
  instance.
- Editing a definition: "_BlockEdit" is interactive; for automation, redefine the
  block from new geometry with rhino_create_block using the same name only after
  confirming with the person, since that changes every instance.
- Exploding an instance (rhino_run_command "_Explode" with select_ids) loses its
  instance user text.
`,
  },

  geometry_hygiene: {
    title: "Geometry hygiene",
    summary: "Closed solids, naked edges, bad objects, joining and capping.",
    body: `
Downstream operations (booleans, sections, volumes, fabrication exports) need clean
geometry.

- Solids: a closed polysurface or closed extrusion. rhino_analyze { op: "measure" }
  reports is_solid; volumes are only meaningful for solids.
- Naked edges mean the solid is open. "_ShowEdges" (interactive) or a Python check
  with Brep.IsSolid / Brep.DuplicateNakedEdgeCurves finds them. Fix with
  "_Join", "_Cap" (planar holes), "_JoinEdge" (last resort), or rebuild the face.
- "_Check" on a selection and "_SelBadObjects" find invalid geometry; fix before
  booleans.
- Join curves before extruding or hatching; open curves won't cap or hatch.
  rhino_geometry_op { op: "join" } joins within document tolerance.
- Keep tolerance consistent: geometry imported from a file with a looser tolerance
  may not join in a tighter file.
- Prefer exact primitives (box, cylinder, extrusion) over meshes for building
  elements; meshes can't boolean reliably in Rhino 7.
- Rebuild noisy curves (rhino_geometry_op rebuild_curve) before lofting; matched
  point counts and directions avoid twisted lofts.
- Booleans fail quietly on coplanar faces and tangent touches; nudge by a small
  amount or split differently.
`,
  },

  python: {
    title: "IronPython 2.7 in Rhino 7",
    summary: "What works, what doesn't, and how rhino_run_python returns data.",
    body: `
Rhino 7 runs IronPython 2.7 (a .NET Python 2). rhino_run_python executes code in
Rhino's own engine with these pre-imported: rhinoscriptsyntax as rs, scriptcontext as
sc, Rhino, System.

- Python 2 syntax: no f-strings, no type hints, no "nonlocal". Use "{} {}".format(a, b).
  print works as a statement or function; output is captured and returned.
- Integer division: 7/2 == 3. Use 7/2.0 or "from __future__ import division".
- Strings: str is bytes-like; use unicode literals (u"…") for non-ASCII text.
- Return data by assigning a variable named result (dicts, lists, numbers, strings).
  Rhino types are converted to strings; Guids to id strings.
- rhinoscriptsyntax (rs) is the simple layer: rs.AddLine, rs.ObjectsByLayer,
  rs.GetUserText / rs.SetUserText, rs.ObjectLayer. It works with Guids.
  rhino_python_help "rs" lists every function; "rs.AddLine" shows its doc.
- RhinoCommon (Rhino.Geometry, Rhino.DocObjects) is the full SDK:
  sc.doc.Objects.AddCurve(curve), Rhino.Geometry.Brep.CreateBooleanUnion(…).
  rhino_api_lookup searches it.
- sc.doc is the active document. Call sc.doc.Views.Redraw() once at the end, not in loops.
- Don't block: no input(), no rs.GetObject prompts, no infinite loops, no long
  sleeps. The script runs on Rhino's UI thread and freezes Rhino until it returns.
  Rhino.RhinoApp.Wait() isn't needed.
- No pip packages; CPython libraries (numpy etc.) are unavailable. The .NET framework is
  available through "import System" and clr.AddReference.
- Each run is one undo record. Wrap risky loops in try/except and report what failed.
`,
  },

  performance: {
    title: "Performance",
    summary: "Batch, avoid redraw storms, query narrowly.",
    body: `
- Batch: rhino_create_geometry, rhino_create_annotations and rhino_insert_blocks take
  up to hundreds of items per call. One call with 200 items is far faster than 200 calls.
- Redraw happens once per call. In Python, disable redraw for big loops:
  rs.EnableRedraw(False) … rs.EnableRedraw(True).
- Query narrowly: filter rhino_query_objects by layer, type, block or user text and
  page with limit/offset; request detail "full" from rhino_get_objects only for the
  ids you need.
- Capture views at modest sizes (1200–1600 px wide) unless detail is needed.
- Long commands (meshing, booleans on large models, rendering) can exceed the default
  120 s timeout; pass timeout_ms when you expect that.
- Hidden layers still cost memory, not display; locked layers still cost display.
`,
  },

  safety: {
    title: "Safety",
    summary: "Undo, backups, and protecting unsaved work.",
    body: `
- Every mutating call is one undo record; rhino_undo reverts it. Undo history is lost
  when the file is closed or a new file is opened.
- Before large or destructive operations (mass delete, redefining blocks, purge,
  changing units), save a copy: rhino_file { action: "save_copy", path: "…_backup.3dm" }.
- Never discard unsaved changes. rhino_file "open"/"new" refuse when the document is
  modified unless discard_changes is true; only set it after the person agrees.
- Don't overwrite the person's files without asking; prefer new file names for exports.
- rhino_run_python and rhino_run_command can do anything Rhino can, including deleting
  files on disk. Keep scripts scoped to the document.
- If Rhino shows a modal dialog, calls time out; ask the person to close it.
- Report what changed: tools return created/deleted/modified ids; summarize them.
`,
  },
};

export const GUIDE_TOPICS = Object.keys(GUIDE);

export function renderGuide(topic?: string): string {
  if (!topic) {
    const index = GUIDE_TOPICS.map((k) => `- ${k}: ${GUIDE[k].summary}`).join("\n");
    return `# ${GUIDE.overview.title}\n${GUIDE.overview.body.trim()}\n\n## Other topics (call rhino_guide with topic)\n${index}`;
  }
  const t = GUIDE[topic];
  if (!t) return `Unknown topic "${topic}". Topics: ${GUIDE_TOPICS.join(", ")}.`;
  return `# ${t.title}\n${t.body.trim()}`;
}
