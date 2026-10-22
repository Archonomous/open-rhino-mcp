// Shared zod shapes mirroring docs/PROTOCOL.md "Shared value shapes".
import { z } from "zod";

export const Point = z
  .array(z.number())
  .min(2)
  .max(3)
  .describe("Point [x, y, z] in model units ([x, y] means z = 0)");
export const Vector = z.array(z.number()).min(2).max(3).describe("Vector [x, y, z]");
export const Point2 = z.array(z.number()).length(2).describe("Page point [x, y] in page units");
export const Id = z.string().describe("Object id (GUID string)");
export const Ids = z.array(Id);

export const Color = z
  .union([z.string().regex(/^#?[0-9a-fA-F]{6}$/), z.array(z.number().int().min(0).max(255)).length(3)])
  .describe('Color "#RRGGBB" or [r, g, b]');

export const Plane = z
  .union([
    z.enum(["world_xy", "world_yz", "world_zx", "cplane"]),
    z.object({
      origin: Point,
      x_axis: Vector.optional(),
      y_axis: Vector.optional(),
      normal: Vector.optional(),
    }),
  ])
  .describe('Plane: "world_xy" (default), "world_yz", "world_zx", "cplane", or {origin, x_axis, y_axis} / {origin, normal}');

export const BBox = z.object({ min: Point, max: Point }).describe("Axis-aligned box {min, max}");

export const UserTextValues = z.record(z.string(), z.union([z.string(), z.null()]));

export const Attributes = z
  .object({
    layer: z.string().optional().describe('Full layer path with "::" (created if missing)'),
    name: z.string().optional(),
    color: Color.optional(),
    color_source: z.enum(["layer", "object", "material", "parent"]).optional(),
    linetype: z.string().optional(),
    print_width: z.number().optional().describe("Print width in mm (0 = default, -1 = no print)"),
    print_color: Color.optional(),
    visible: z.boolean().optional(),
    locked: z.boolean().optional(),
    user_text: UserTextValues.optional().describe("Key/value tags. null deletes a key (set_attributes only)."),
    groups: z.array(z.string()).optional().describe("Group names to add the object to (created if missing)"),
    layout: z
      .string()
      .optional()
      .describe('Layout (page) name to place the object in page space, e.g. a title block or sheet note. "model" moves it back to model space.'),
  })
  .describe("Object attributes; all optional");

export const ObjectType = z.enum([
  "curve",
  "brep",
  "extrusion",
  "mesh",
  "subd",
  "point",
  "pointcloud",
  "text",
  "leader",
  "dimension",
  "textdot",
  "hatch",
  "block_instance",
  "detail",
  "light",
  "other",
]);

export const TimeoutMs = z
  .number()
  .int()
  .min(1000)
  .max(600_000)
  .optional()
  .describe("Override the 120 s timeout for slow operations (ms, max 600000)");

// ---- geometry.create items -------------------------------------------------
const withAttrs = { attributes: Attributes.optional() };

export const GeometryItem = z.discriminatedUnion("type", [
  z.object({ type: z.literal("point"), point: Point, ...withAttrs }),
  z.object({ type: z.literal("line"), start: Point, end: Point, ...withAttrs }),
  z.object({ type: z.literal("polyline"), points: z.array(Point).min(2), closed: z.boolean().optional(), ...withAttrs }),
  z.object({
    type: z.literal("rectangle"),
    plane: Plane.optional(),
    corner: Point.optional().describe("Lower-left corner (default: plane origin)"),
    width: z.number(),
    height: z.number(),
    ...withAttrs,
  }),
  z.object({ type: z.literal("circle"), plane: Plane.optional(), center: Point.optional(), radius: z.number().positive(), ...withAttrs }),
  z.object({
    type: z.literal("arc"),
    start: Point.optional().describe("3-point form: start, mid, end"),
    mid: Point.optional(),
    end: Point.optional(),
    plane: Plane.optional().describe("Center form: plane, center, radius, start_angle_deg, end_angle_deg"),
    center: Point.optional(),
    radius: z.number().positive().optional(),
    start_angle_deg: z.number().optional(),
    end_angle_deg: z.number().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("ellipse"),
    plane: Plane.optional(),
    center: Point.optional(),
    radius_x: z.number().positive(),
    radius_y: z.number().positive(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("polygon"),
    plane: Plane.optional(),
    center: Point.optional(),
    radius: z.number().positive(),
    sides: z.number().int().min(3),
    circumscribed: z.boolean().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("curve"),
    points: z.array(Point).min(2),
    degree: z.number().int().min(1).max(11).optional(),
    interpolate: z.boolean().optional().describe("true: curve passes through points; false: points are control points"),
    closed: z.boolean().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("box"),
    corner_a: Point.optional().describe("Two-corner form: corner_a, corner_b"),
    corner_b: Point.optional(),
    plane: Plane.optional().describe("Size form: plane, corner, size [x,y,z]"),
    corner: Point.optional(),
    size: z.array(z.number()).length(3).optional(),
    ...withAttrs,
  }),
  z.object({ type: z.literal("sphere"), center: Point, radius: z.number().positive(), ...withAttrs }),
  z.object({
    type: z.literal("cylinder"),
    plane: Plane.optional(),
    center: Point.optional(),
    radius: z.number().positive(),
    height: z.number(),
    cap: z.boolean().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("cone"),
    plane: Plane.optional(),
    center: Point.optional(),
    radius: z.number().positive(),
    height: z.number(),
    cap: z.boolean().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("plane_surface"),
    plane: Plane.optional(),
    corner: Point.optional(),
    width: z.number(),
    height: z.number(),
    ...withAttrs,
  }),
]);

// ---- annotations.create items ---------------------------------------------
export const Justification = z.enum([
  "left",
  "center",
  "right",
  "bottom_left",
  "bottom_center",
  "bottom_right",
  "middle_left",
  "middle_center",
  "middle_right",
  "top_left",
  "top_center",
  "top_right",
]);

export const AnnotationItem = z.discriminatedUnion("type", [
  z.object({
    type: z.literal("text"),
    text: z.string(),
    point: Point,
    height: z.number().positive().optional().describe("Text height in the space it's drawn in (model or page units)"),
    plane: Plane.optional(),
    dimstyle: z.string().optional(),
    justification: Justification.optional(),
    font: z.string().optional(),
    rotation_deg: z.number().optional(),
    wrap_width: z.number().positive().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("leader"),
    text: z.string(),
    points: z.array(Point).min(2).describe("Arrow tip first"),
    plane: Plane.optional(),
    dimstyle: z.string().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("dimension"),
    kind: z.enum(["aligned", "horizontal", "vertical"]).optional(),
    start: Point,
    end: Point,
    line_point: Point.describe("A point the dimension line passes through"),
    plane: Plane.optional(),
    dimstyle: z.string().optional(),
    ...withAttrs,
  }),
  z.object({
    type: z.literal("dot"),
    text: z.string(),
    point: Point,
    height: z.number().int().positive().optional().describe("Font height in screen pixels (default 14)"),
    ...withAttrs,
  }),
]);

export const DetailSpec = z.object({
  corner_a: Point2,
  corner_b: Point2,
  projection: z.enum(["top", "bottom", "left", "right", "front", "back", "perspective"]).optional(),
  scale: z
    .object({
      model: z.number().positive(),
      model_units: z.string().describe('e.g. "feet", "inches", "millimeters", "meters"'),
      page: z.number().positive(),
      page_units: z.string(),
    })
    .optional()
    .describe('Detail scale, e.g. 1" = 1\'-6" is {model: 1.5, model_units: "feet", page: 1, page_units: "inches"}'),
  zoom_ids: Ids.optional().describe("Objects to center the detail on before applying scale"),
  lock: z.boolean().optional(),
  name: z.string().optional(),
});
