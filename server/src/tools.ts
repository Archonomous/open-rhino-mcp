// Tool definitions: each maps 1:1 to a bridge method in docs/PROTOCOL.md, plus two
// Node-only tools (rhino_command_help, rhino_guide).
import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult, ToolAnnotations } from "@modelcontextprotocol/sdk/types.js";
import { BridgeError, NOT_CONNECTED_MESSAGE } from "./bridge.js";
import { fetchCommandHelp, CommandHelpError, type FetchLike } from "./docs.js";
import { GUIDE_TOPICS, renderGuide } from "./guide.js";
import {
  AnnotationItem,
  Attributes,
  BBox,
  Color,
  DetailSpec,
  GeometryItem,
  Id,
  Ids,
  ObjectType,
  Plane,
  Point,
  TimeoutMs,
  UserTextValues,
  Vector,
} from "./schemas.js";

export interface BridgeLike {
  call<T = unknown>(method: string, params?: Record<string, unknown>, timeoutMs?: number): Promise<T>;
}

type Shape = Record<string, z.ZodType>;

interface ToolSpec {
  name: string;
  title: string;
  method: string;
  description: string;
  shape: Shape;
  annotations: ToolAnnotations;
  /** Custom result formatting. Default: JSON text. */
  format?: (result: unknown, args: Record<string, unknown>) => CallToolResult;
}

export const MAX_RESULT_CHARS = 100_000;

export function jsonText(value: unknown): string {
  let text = JSON.stringify(value, null, 2) ?? "null";
  if (text.length > 20_000) text = JSON.stringify(value) ?? "null";
  if (text.length > MAX_RESULT_CHARS) {
    text =
      text.slice(0, MAX_RESULT_CHARS) +
      `\n…[truncated ${text.length - MAX_RESULT_CHARS} characters. Narrow the request: use filters, limit/offset, or fewer ids.]`;
  }
  return text;
}

function ok(result: unknown): CallToolResult {
  return { content: [{ type: "text", text: jsonText(result) }] };
}

export function errorResult(message: string): CallToolResult {
  return { content: [{ type: "text", text: message }], isError: true };
}

const READ: ToolAnnotations = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const ADD: ToolAnnotations = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const EDIT: ToolAnnotations = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false };
const EDIT_IDEMPOTENT: ToolAnnotations = { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false };
const ANYTHING: ToolAnnotations = { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: true };

const CHANGESET_NOTE = "Returns created/deleted/modified ids. One undo step.";

export const TOOL_SPECS: ToolSpec[] = [
  // ---------------------------------------------------------------- system
  {
    name: "rhino_status",
    title: "Rhino status",
    method: "system.status",
    description:
      "Check the Rhino connection and read the essentials: Rhino version, document name/path, unsaved changes, model units, absolute and angle tolerance, object and layer counts, current layer, active view, and whether a command is running. Call this first in a session; units and tolerance change how every coordinate should be read.",
    shape: {},
    annotations: READ,
  },
  // -------------------------------------------------------------- commands
  {
    name: "rhino_list_commands",
    title: "List Rhino commands",
    method: "commands.list",
    description:
      "List the English names of every loaded Rhino command, including plug-in commands. Filter by substring (case-insensitive), e.g. {filter: \"Hatch\"}. Use with rhino_command_help to learn options before scripting a command with rhino_run_command.",
    shape: {
      filter: z.string().optional().describe("Case-insensitive substring"),
      limit: z.number().int().min(1).max(5000).optional().describe("Default 2000"),
    },
    annotations: READ,
  },
  {
    name: "rhino_run_command",
    title: "Run a Rhino command macro",
    method: "commands.run",
    description:
      'Run any Rhino command as a scripted macro (RhinoApp.RunScript), e.g. "-_Hatch _Pattern=Grid _Enter" or "-_SaveAs \\"C:\\\\path\\\\file.3dm\\" _Enter". Use English names with "_" and dash forms ("-_") to avoid dialogs; finish prompts with _Enter; never use _Pause. Pass select_ids to select exactly those objects first, for commands that act on the selection. Returns success, the command-line history printed during the run (read it: Rhino reports failures there), and created/deleted/modified ids. Fails if another command is already running. See rhino_guide topic "scripting_commands".',
    shape: {
      macro: z.string().min(1).describe("Command macro"),
      select_ids: Ids.optional().describe("Replace the selection with these objects before running"),
      echo: z.boolean().optional().describe("Echo the macro to the command line (default false)"),
      capture_history: z.boolean().optional().describe("Return command-line text produced during the run (default true)"),
      timeout_ms: TimeoutMs,
    },
    annotations: ANYTHING,
  },
  {
    name: "rhino_command_history",
    title: "Read command history",
    method: "commands.history",
    description: "Read the last lines of Rhino's command-line history (what Rhino printed, including errors and prompts).",
    shape: { lines: z.number().int().min(1).max(2000).optional().describe("Default 50") },
    annotations: READ,
  },
  // ---------------------------------------------------------------- python
  {
    name: "rhino_run_python",
    title: "Run IronPython in Rhino",
    method: "python.run",
    description:
      "Run IronPython 2.7 inside Rhino with rhinoscriptsyntax (rs), scriptcontext (sc), Rhino and System pre-imported. Python 2 syntax only (no f-strings). print output is captured. Assign a variable named `result` to return data (dicts/lists/numbers/strings; Guids become id strings). `inputs` become globals. Runs on Rhino's UI thread: no prompts (rs.GetObject etc.), no infinite loops. One undo step; returns created/deleted/modified ids. A script error comes back as an error with the traceback. See rhino_guide topic \"python\".",
    shape: {
      code: z.string().min(1).describe("IronPython 2.7 source"),
      inputs: z.record(z.string(), z.unknown()).optional().describe("Values set as global variables before running"),
      timeout_ms: TimeoutMs,
    },
    annotations: ANYTHING,
    format: (result) => {
      const r = (result ?? {}) as Record<string, unknown>;
      if (r.ok === false) {
        const parts = [`Python error: ${r.error ?? "unknown error"}`];
        if (r.traceback) parts.push(String(r.traceback));
        if (r.output) parts.push(`Output before the error:\n${r.output}`);
        parts.push(jsonText({ created_ids: r.created_ids, deleted_ids: r.deleted_ids, modified_ids: r.modified_ids }));
        return { content: [{ type: "text", text: parts.join("\n\n") }], isError: true };
      }
      return ok(result);
    },
  },
  {
    name: "rhino_python_help",
    title: "Python API help",
    method: "python.help",
    description:
      'Read live documentation from Rhino\'s Python environment: "rs" lists every rhinoscriptsyntax function, "rs.AddLine" returns its signature and docstring, "Rhino.Geometry.Brep" lists members. Use before writing rhino_run_python code that calls unfamiliar functions.',
    shape: { name: z.string().min(1).describe('Dotted name, e.g. "rs", "rs.ObjectsByLayer", "Rhino.Geometry.Curve"') },
    annotations: READ,
  },
  {
    name: "rhino_api_lookup",
    title: "RhinoCommon API lookup",
    method: "api.reflect",
    description:
      'Search the RhinoCommon SDK loaded in this Rhino by type name (case-insensitive substring), e.g. {query: "LinearDimension"} or {query: "Brep", member: "Boolean"}. Returns matching public types; member signatures are included when one type matches or when member is given. Use to write correct RhinoCommon calls in rhino_run_python.',
    shape: {
      query: z.string().min(1),
      member: z.string().optional().describe("Filter members by substring"),
      limit: z.number().int().min(1).max(200).optional().describe("Default 25"),
    },
    annotations: READ,
  },
  // ------------------------------------------------------------------- doc
  {
    name: "rhino_document_info",
    title: "Document info",
    method: "doc.info",
    description:
      "Everything about the open document in one call: status fields plus layouts, named views, block count, dimension styles (and current), linetypes, materials count, groups, document user text and notes.",
    shape: {},
    annotations: READ,
  },
  {
    name: "rhino_file",
    title: "Open, save, import, export",
    method: "doc.file",
    description:
      'File operations via scripted commands. action: "new" | "open" | "save" | "save_as" | "save_copy" (writes a copy, keeps the current file open) | "import" | "export_selected" (exports select_ids, or the current selection, to path; format from the extension, e.g. .dwg, .dxf, .pdf, .obj, .stl). "new"/"open" refuse when the document has unsaved changes unless discard_changes is true — only set that after the person agrees. Use full paths.',
    shape: {
      action: z.enum(["new", "open", "save", "save_as", "import", "export_selected", "save_copy"]),
      path: z.string().optional().describe("Full file path (required except for save/new)"),
      select_ids: Ids.optional().describe("For export_selected"),
      discard_changes: z.boolean().optional(),
      timeout_ms: TimeoutMs,
    },
    annotations: ANYTHING,
  },
  {
    name: "rhino_undo",
    title: "Undo",
    method: "doc.undo",
    description: "Undo the last step(s). Each Open Rhino MCP mutating call is one step.",
    shape: { steps: z.number().int().min(1).max(100).optional() },
    annotations: EDIT,
  },
  {
    name: "rhino_redo",
    title: "Redo",
    method: "doc.redo",
    description: "Redo step(s) undone with rhino_undo.",
    shape: { steps: z.number().int().min(1).max(100).optional() },
    annotations: EDIT,
  },
  // ---------------------------------------------------------------- layers
  {
    name: "rhino_list_layers",
    title: "List layers",
    method: "layers.list",
    description:
      'List all layers with full path ("Parent::Child"), color, visibility, lock, linetype, print width/color and object count, plus the current layer.',
    shape: { include_counts: z.boolean().optional().describe("Include object counts (default true)") },
    annotations: READ,
  },
  {
    name: "rhino_upsert_layer",
    title: "Create or update a layer",
    method: "layers.upsert",
    description:
      'Create a layer by full path (parents created as needed) or update an existing one: color, visibility, lock, linetype, print width (mm), print color, make it current, or rename (rename_to is the new short name).',
    shape: {
      path: z.string().min(1).describe('Full path, e.g. "E-LITE::Fixtures"'),
      color: Color.optional(),
      visible: z.boolean().optional(),
      locked: z.boolean().optional(),
      linetype: z.string().optional(),
      print_width: z.number().optional(),
      print_color: Color.optional(),
      make_current: z.boolean().optional(),
      rename_to: z.string().optional(),
    },
    annotations: EDIT_IDEMPOTENT,
  },
  {
    name: "rhino_delete_layer",
    title: "Delete a layer",
    method: "layers.delete",
    description:
      "Delete a layer. Fails if it holds objects unless delete_objects is true (which deletes them too), or if it is the current layer.",
    shape: { path: z.string().min(1), delete_objects: z.boolean().optional() },
    annotations: EDIT,
  },
  // --------------------------------------------------------------- objects
  {
    name: "rhino_query_objects",
    title: "Find objects",
    method: "objects.query",
    description:
      'Find objects by any combination of filters (AND-ed): ids, types, layer (with sublayers by default), name glob ("R-*"), user_text ({key: value} or {key: "*"} for "has key"), block definition name, selected_only, bbox intersection, text_contains (annotation text). Returns summaries (id, type, layer, name, color, bbox, user text, block name, text, curve length). Paged: limit (default 200) and offset; total gives the full count. Hidden objects are excluded unless include_hidden.',
    shape: {
      ids: Ids.optional(),
      types: z.array(ObjectType).optional(),
      layer: z.string().optional().describe("Full layer path"),
      include_sublayers: z.boolean().optional(),
      name: z.string().optional().describe("Glob on object name (* and ?)"),
      user_text: z.record(z.string(), z.string()).optional().describe('{key: value} exact, or {key: "*"} any value'),
      block: z.string().optional().describe("Block definition name (block instances only)"),
      selected_only: z.boolean().optional(),
      include_hidden: z.boolean().optional(),
      include_locked: z.boolean().optional(),
      bbox: BBox.optional(),
      text_contains: z.string().optional(),
      limit: z.number().int().min(1).max(5000).optional(),
      offset: z.number().int().min(0).optional(),
    },
    annotations: READ,
  },
  {
    name: "rhino_get_objects",
    title: "Get object details",
    method: "objects.get",
    description:
      'Read objects by id. detail "summary" (default) matches rhino_query_objects; "full" adds geometry data: points, line ends, polyline vertices, circle/arc center/radius/plane, NURBS degree and control points, brep faces/edges/solid/area/volume, mesh counts, text content/plane/height/dimstyle, dimension value, block instance insertion point/transform/definition.',
    shape: { ids: Ids.min(1), detail: z.enum(["summary", "full"]).optional() },
    annotations: READ,
  },
  {
    name: "rhino_select_objects",
    title: "Select objects",
    method: "objects.select",
    description:
      'Change the selection in Rhino: mode "replace" (default), "add", "remove", or "clear". Useful before commands that act on the selection, and to show the person which objects you mean.',
    shape: { ids: Ids.optional(), mode: z.enum(["replace", "add", "remove", "clear"]).optional() },
    annotations: { ...READ, readOnlyHint: false, idempotentHint: true },
  },
  {
    name: "rhino_delete_objects",
    title: "Delete objects",
    method: "objects.delete",
    description: `Delete objects by id. ${CHANGESET_NOTE}`,
    shape: { ids: Ids.min(1) },
    annotations: EDIT_IDEMPOTENT,
  },
  {
    name: "rhino_set_attributes",
    title: "Set object attributes",
    method: "objects.set_attributes",
    description: `Change attributes on objects: layer (full path, created if missing), name, color and color source, linetype, print width/color, visibility, lock, groups, and user text (merged; a null value deletes that key). ${CHANGESET_NOTE}`,
    shape: { ids: Ids.min(1), attributes: Attributes },
    annotations: EDIT_IDEMPOTENT,
  },
  {
    name: "rhino_transform_objects",
    title: "Move, rotate, scale, mirror",
    method: "objects.transform",
    description: `Transform objects. op "move" (translation), "rotate" (angle_deg about axis through center; axis default Z), "scale" (factor number or [x,y,z] about center), "mirror" (across plane; default the world YZ plane through center), "matrix" (4x4 row-major). copy: true keeps the originals. ${CHANGESET_NOTE}`,
    shape: {
      ids: Ids.min(1),
      op: z.enum(["move", "rotate", "scale", "mirror", "matrix"]),
      copy: z.boolean().optional(),
      translation: Vector.optional(),
      angle_deg: z.number().optional(),
      axis: Vector.optional(),
      center: Point.optional(),
      factor: z.union([z.number(), z.array(z.number()).length(3)]).optional(),
      plane: Plane.optional(),
      matrix: z.array(z.array(z.number()).length(4)).length(4).optional(),
    },
    annotations: EDIT,
  },
  // -------------------------------------------------------------- usertext
  {
    name: "rhino_get_user_text",
    title: "Read user text",
    method: "usertext.get",
    description:
      "Read user text (key/value tags) from objects and/or the document. Optionally limit to certain keys. User text is the recommended way to tag objects (circuit, VA, zone…) — see rhino_guide topic \"layers_attributes\".",
    shape: {
      ids: Ids.optional(),
      document: z.boolean().optional().describe("Include document-level user text"),
      keys: z.array(z.string()).optional(),
    },
    annotations: READ,
  },
  {
    name: "rhino_set_user_text",
    title: "Write user text",
    method: "usertext.set",
    description: `Write user text on objects (ids) and/or the document. mode "merge" (default) updates the given keys (null deletes a key); "replace" clears existing keys first. ${CHANGESET_NOTE}`,
    shape: {
      ids: Ids.optional(),
      document: z.boolean().optional(),
      values: UserTextValues,
      mode: z.enum(["merge", "replace"]).optional(),
    },
    annotations: EDIT_IDEMPOTENT,
  },
  // -------------------------------------------------------------- geometry
  {
    name: "rhino_create_geometry",
    title: "Create geometry",
    method: "geometry.create",
    description:
      "Create up to 500 objects in one call: point, line, polyline, rectangle, circle, arc (3-point or center/angles), ellipse, polygon, curve (interpolated or control points), box (two corners or plane+size), sphere, cylinder, cone, plane_surface. Each item may carry attributes (layer, name, color, user_text…). Coordinates are model units. Returns ids in item order (null where an item failed) plus errors. For text, leaders and dimensions use rhino_create_annotations.",
    shape: { items: z.array(GeometryItem).min(1).max(500) },
    annotations: ADD,
  },
  {
    name: "rhino_geometry_op",
    title: "Geometry operation",
    method: "geometry.op",
    description: `Build or edit geometry from existing objects. op: extrude {ids, direction | height, cap}; loft {ids in order, closed, loft_type}; revolve {ids, axis_start, axis_end, start_deg, end_deg}; sweep1 {rail_id, profile_ids}; sweep2 {rail_ids [2], profile_ids}; pipe {ids, radius, cap}; offset_curve {ids, distance, plane, corner}; join {ids}; explode {ids}; cap {ids}; fillet_curves {ids [2], radius}; rebuild_curve {ids, point_count, degree}; project {ids, target_ids, direction}; split_curve {ids, cutter_ids}; trim_curve {ids, cutter_ids, keep_point}. delete_input removes the inputs; attributes apply to results. ${CHANGESET_NOTE}`,
    shape: {
      op: z.enum([
        "extrude",
        "loft",
        "revolve",
        "sweep1",
        "sweep2",
        "pipe",
        "offset_curve",
        "join",
        "explode",
        "cap",
        "fillet_curves",
        "rebuild_curve",
        "project",
        "split_curve",
        "trim_curve",
      ]),
      ids: Ids.optional(),
      delete_input: z.boolean().optional(),
      attributes: Attributes.optional(),
      direction: Vector.optional(),
      height: z.number().optional(),
      cap: z.union([z.boolean(), z.enum(["none", "flat", "round"])]).optional().describe("extrude: boolean; pipe: none|flat|round"),
      closed: z.boolean().optional(),
      loft_type: z.enum(["normal", "loose", "tight", "straight"]).optional(),
      axis_start: Point.optional(),
      axis_end: Point.optional(),
      start_deg: z.number().optional(),
      end_deg: z.number().optional(),
      rail_id: Id.optional(),
      rail_ids: Ids.optional(),
      profile_ids: Ids.optional(),
      radius: z.number().optional(),
      distance: z.number().optional(),
      plane: Plane.optional(),
      corner: z.enum(["sharp", "round", "smooth", "chamfer"]).optional(),
      point_count: z.number().int().min(2).optional(),
      degree: z.number().int().min(1).max(11).optional(),
      target_ids: Ids.optional(),
      cutter_ids: Ids.optional(),
      keep_point: Point.optional(),
      timeout_ms: TimeoutMs,
    },
    annotations: EDIT,
  },
  {
    name: "rhino_boolean",
    title: "Boolean operation",
    method: "geometry.boolean",
    description: `Boolean union, difference (a minus b), intersection, or split (a split by b). Works on closed breps/extrusions, or on closed planar curves for 2D region booleans. delete_input defaults to true. ${CHANGESET_NOTE}`,
    shape: {
      op: z.enum(["union", "difference", "intersection", "split"]),
      a_ids: Ids.min(1),
      b_ids: Ids.optional(),
      delete_input: z.boolean().optional(),
      attributes: Attributes.optional(),
      timeout_ms: TimeoutMs,
    },
    annotations: EDIT,
  },
  {
    name: "rhino_analyze",
    title: "Measure and analyze",
    method: "geometry.analyze",
    description:
      'Read-only analysis. op "measure" {ids}: length, area, volume, centroid, bbox, closed/solid flags. "intersect" {ids: [a, b]}: intersection points and curves. "closest_point" {ids: [one], point}: closest point and distance. "distance" {point, point_b}.',
    shape: {
      op: z.enum(["measure", "intersect", "closest_point", "distance"]),
      ids: Ids.optional(),
      point: Point.optional(),
      point_b: Point.optional(),
    },
    annotations: READ,
  },
  // ---------------------------------------------------------------- blocks
  {
    name: "rhino_list_blocks",
    title: "List block definitions",
    method: "blocks.list",
    description: "List block definitions with description, object count, top-level instance count, and linked status/path.",
    shape: { include_counts: z.boolean().optional() },
    annotations: READ,
  },
  {
    name: "rhino_create_block",
    title: "Create a block definition",
    method: "blocks.create",
    description: `Define a block from objects with a base (insertion) point. By default the source objects are deleted and replaced by one instance at the same place. ${CHANGESET_NOTE}`,
    shape: {
      name: z.string().min(1),
      ids: Ids.min(1),
      base_point: Point,
      description: z.string().optional(),
      delete_input: z.boolean().optional(),
      replace_with_instance: z.boolean().optional(),
    },
    annotations: ADD,
  },
  {
    name: "rhino_insert_blocks",
    title: "Insert block instances",
    method: "blocks.insert",
    description:
      "Insert block instances in bulk: name, point, rotation_deg (about Z), scale (number or [x,y,z]), attributes (layer, user_text tags such as circuit/VA…). Returns ids in order plus errors.",
    shape: {
      items: z
        .array(
          z.object({
            name: z.string().min(1),
            point: Point,
            rotation_deg: z.number().optional(),
            scale: z.union([z.number(), z.array(z.number()).length(3)]).optional(),
            attributes: Attributes.optional(),
          }),
        )
        .min(1)
        .max(1000),
    },
    annotations: ADD,
  },
  // ----------------------------------------------------------- annotations
  {
    name: "rhino_create_annotations",
    title: "Create text, leaders, dimensions, dots",
    method: "annotations.create",
    description:
      'Create annotations in bulk: text {text, point, height, plane, dimstyle, justification, font, rotation_deg, wrap_width}, leader {text, points (arrow tip first)}, dimension {kind aligned|horizontal|vertical, start, end, line_point}, dot {text, point}. To put text on a layout sheet, activate the layout first with rhino_set_view {action: "activate", view: "<layout name>"} — coordinates are then page units. Returns ids and errors.',
    shape: { items: z.array(AnnotationItem).min(1).max(500) },
    annotations: ADD,
  },
  {
    name: "rhino_edit_annotations",
    title: "Edit annotation text",
    method: "annotations.edit",
    description: `Edit text/leader/dot/dimension text: set text outright, or find/replace within it; optionally change height or dimstyle. Editing rebuilds the text as plain text (rich formatting on edited objects is lost). ${CHANGESET_NOTE}`,
    shape: {
      ids: Ids.min(1),
      text: z.string().optional(),
      find: z.string().optional(),
      replace: z.string().optional(),
      height: z.number().positive().optional(),
      dimstyle: z.string().optional(),
    },
    annotations: EDIT,
  },
  // ------------------------------------------------------- views & output
  {
    name: "rhino_list_views",
    title: "List views and layouts",
    method: "views.list",
    description: "List model views and layout pages (with page size and details: scale, projection, lock), named views, and display modes.",
    shape: {},
    annotations: READ,
  },
  {
    name: "rhino_set_view",
    title: "Change a view",
    method: "views.set",
    description:
      'Change what a view shows. action: "activate" (make a model view or layout active), "zoom_extents" (all_views optional), "zoom_selected", "zoom_ids" {ids}, "projection" {projection}, "named_view" {named_view} restore, "save_named_view" {named_view}, "display_mode" {display_mode, e.g. Wireframe, Shaded, Rendered, Technical, Pen}. view defaults to the active view.',
    shape: {
      view: z.string().optional(),
      action: z.enum(["activate", "zoom_extents", "zoom_selected", "zoom_ids", "projection", "named_view", "display_mode", "save_named_view"]),
      ids: Ids.optional(),
      projection: z.enum(["top", "bottom", "left", "right", "front", "back", "perspective", "two_point_perspective"]).optional(),
      named_view: z.string().optional(),
      display_mode: z.string().optional(),
      all_views: z.boolean().optional(),
    },
    annotations: { ...READ, readOnlyHint: false, idempotentHint: true },
  },
  {
    name: "rhino_capture_view",
    title: "Capture a view image",
    method: "views.capture",
    description:
      "Capture a model view or layout as a PNG image you can look at. Use it to check your work. Options: view name (default active), width/height in pixels (default 1600x1000), display_mode, transparent background, zoom_ids to frame specific objects first, grid/axes.",
    shape: {
      view: z.string().optional(),
      width: z.number().int().min(64).max(8000).optional(),
      height: z.number().int().min(64).max(8000).optional(),
      display_mode: z.string().optional(),
      transparent: z.boolean().optional(),
      zoom_ids: Ids.optional(),
      grid: z.boolean().optional(),
      axes: z.boolean().optional(),
      timeout_ms: TimeoutMs,
    },
    annotations: READ,
    format: (result) => {
      const r = (result ?? {}) as { view?: string; width?: number; height?: number; png_base64?: string };
      if (!r.png_base64) return errorResult("Rhino returned no image data for the capture.");
      return {
        content: [
          { type: "image", data: r.png_base64, mimeType: "image/png" },
          { type: "text", text: `Captured view "${r.view ?? "?"}" at ${r.width ?? "?"}×${r.height ?? "?"} px.` },
        ],
      };
    },
  },
  {
    name: "rhino_create_layout",
    title: "Create a layout sheet",
    method: "layouts.create",
    description:
      'Create a layout (page view) of width × height in page units with optional detail views: corners in page units, projection, scale (e.g. 1" = 1\'-6" → {model: 1.5, model_units: "feet", page: 1, page_units: "inches"}), zoom_ids to frame objects, lock. Returns the view name and detail ids. See rhino_guide topic "drafting".',
    shape: {
      name: z.string().min(1),
      width: z.number().positive(),
      height: z.number().positive(),
      details: z.array(DetailSpec).optional(),
    },
    annotations: ADD,
  },
  {
    name: "rhino_export_pdf",
    title: "Export PDF",
    method: "export.pdf",
    description:
      'Write a PDF. Default: every layout in page order; or name specific layouts; or give view (a model view) with width/height in inches. dpi default 300; color "display", "print" (honor print colors, default for sheets) or "black_white". Use a full path and don\'t overwrite the person\'s files without asking.',
    shape: {
      path: z.string().min(1),
      layouts: z.array(z.string()).optional(),
      view: z.string().optional(),
      dpi: z.number().int().min(72).max(1200).optional(),
      color: z.enum(["display", "print", "black_white"]).optional(),
      width: z.number().positive().optional(),
      height: z.number().positive().optional(),
      timeout_ms: TimeoutMs,
    },
    annotations: { ...ADD, idempotentHint: true },
  },
];

export const NODE_TOOL_NAMES = ["rhino_command_help", "rhino_guide"];
export const ALL_TOOL_NAMES = [...TOOL_SPECS.map((t) => t.name), ...NODE_TOOL_NAMES];

/** Strip undefined so the plug-in sees only what the agent set. */
function clean(args: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(args)) if (v !== undefined) out[k] = v;
  return out;
}

export function bridgeErrorToResult(e: unknown): CallToolResult {
  if (e instanceof BridgeError) {
    let text = e.message;
    if (e.details !== undefined && e.details !== null) text += `\nDetails: ${jsonText(e.details)}`;
    return errorResult(text);
  }
  const msg = e instanceof Error ? e.message : String(e);
  return errorResult(`Unexpected error talking to Rhino: ${msg}. ${NOT_CONNECTED_MESSAGE}`);
}

export interface RegisterOptions {
  fetchImpl?: FetchLike;
}

export function registerTools(server: McpServer, bridge: BridgeLike, opts: RegisterOptions = {}): void {
  for (const spec of TOOL_SPECS) {
    server.registerTool(
      spec.name,
      { title: spec.title, description: spec.description, inputSchema: spec.shape, annotations: spec.annotations },
      async (args: Record<string, unknown>) => {
        const params = clean(args ?? {});
        const timeout = typeof params.timeout_ms === "number" ? (params.timeout_ms as number) : undefined;
        try {
          const result = await bridge.call(spec.method, params, timeout !== undefined ? timeout + 5000 : undefined);
          return spec.format ? spec.format(result, params) : ok(result);
        } catch (e) {
          return bridgeErrorToResult(e);
        }
      },
    );
  }

  const fetchImpl: FetchLike = opts.fetchImpl ?? ((url) => fetch(url, { headers: { "User-Agent": "open-rhino-mcp" } }));
  server.registerTool(
    "rhino_command_help",
    {
      title: "Rhino command help",
      description:
        'Read the official Rhino 7 help page for a command (options, steps, notes), e.g. {command: "Hatch"} or "-Export". Use before scripting an unfamiliar command with rhino_run_command. Fetched from docs.mcneel.com (needs internet).',
      inputSchema: { command: z.string().min(1).describe("Command name, e.g. Hatch, Make2D, ViewCaptureToFile") },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: true },
    },
    async ({ command }: { command: string }) => {
      try {
        return { content: [{ type: "text", text: await fetchCommandHelp(command, fetchImpl) }] };
      } catch (e) {
        return errorResult(e instanceof CommandHelpError ? e.message : `Couldn't load help: ${(e as Error).message}`);
      }
    },
  );

  server.registerTool(
    "rhino_guide",
    {
      title: "Rhino 7 best practices",
      description: `Best-practice notes for working in Rhino 7 through these tools. No topic returns the overview and topic list. Topics: ${GUIDE_TOPICS.join(", ")}.`,
      inputSchema: { topic: z.enum(GUIDE_TOPICS as [string, ...string[]]).optional() },
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ topic }: { topic?: string }) => ({ content: [{ type: "text", text: renderGuide(topic) }] }),
  );
}
