// Open Rhino MCP — stdio MCP server that drives Rhino 7 through the Open Rhino MCP plug-in.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { RhinoBridge } from "./bridge.js";
import { GUIDE, GUIDE_TOPICS, renderGuide } from "./guide.js";
import { registerTools } from "./tools.js";

export const VERSION = "0.1.4";

export const INSTRUCTIONS = `Open Rhino MCP drives a running Rhino 7 (Windows/macOS) through its plug-in.
- Start with rhino_status: model units and tolerance decide how to read every coordinate.
- Prefer typed tools (query/create/annotate/blocks/layouts/pdf), then rhino_run_command for any Rhino command (dash-scripted, English "_" names, finish with _Enter, never _Pause), then rhino_run_python (IronPython 2.7: Python 2 syntax, rs/sc/Rhino pre-imported, assign result to return data).
- Learn before scripting: rhino_list_commands, rhino_command_help (official Rhino 7 docs), rhino_python_help, rhino_api_lookup (live RhinoCommon), rhino_guide (best practices by topic).
- Every mutating call is one undo step (rhino_undo reverts it) and returns created/deleted/modified ids.
- Tag objects with user text (e.g. circuit=11, VA=180) and query by tags rather than names.
- Check visual work with rhino_capture_view before reporting it done.
- Never discard unsaved work or overwrite files without the person's OK.`;

export function createServer(bridge = new RhinoBridge()): McpServer {
  const server = new McpServer(
    { name: "open-rhino-mcp", title: "Open Rhino MCP", version: VERSION },
    { instructions: INSTRUCTIONS },
  );

  registerTools(server, bridge);

  for (const topic of GUIDE_TOPICS) {
    const t = GUIDE[topic];
    server.registerResource(
      `guide-${topic}`,
      `rhino7://guide/${topic}`,
      { title: t.title, description: t.summary, mimeType: "text/markdown" },
      async (uri) => ({ contents: [{ uri: uri.href, mimeType: "text/markdown", text: renderGuide(topic) }] }),
    );
  }

  server.registerPrompt(
    "draft_check",
    {
      title: "Check a drawing sheet",
      description: "Review a layout sheet: capture it, read its annotations, and list problems before printing.",
      argsSchema: { layout: z.string().optional().describe("Layout name (default: all layouts)") },
    },
    ({ layout }) => ({
      messages: [
        {
          role: "user",
          content: {
            type: "text",
            text: `Review ${layout ? `the layout "${layout}"` : "every layout"} in the open Rhino document before it goes out.
1. rhino_status and rhino_list_views to get units, layouts and detail scales.
2. rhino_capture_view each layout and look at it.
3. rhino_query_objects for text/leader/dimension objects to read annotation text.
4. Report: overlapping or clipped text, unlocked details, inconsistent scales or dimstyles, empty or missing title-block fields, objects on wrong layers, anything that wouldn't print.
Don't change anything; list findings with object ids.`,
          },
        },
      ],
    }),
  );

  server.registerPrompt(
    "schedule_from_tags",
    {
      title: "Build a schedule from user-text tags",
      description: "Count tagged objects (e.g. by circuit) and summarize them as a table.",
      argsSchema: {
        key: z.string().describe('User text key to group by, e.g. "circuit"'),
        block: z.string().optional().describe("Limit to instances of this block"),
      },
    },
    ({ key, block }) => ({
      messages: [
        {
          role: "user",
          content: {
            type: "text",
            text: `Build a schedule grouped by the user-text key "${key}"${block ? ` for block "${block}"` : ""}.
Use rhino_query_objects with user_text {"${key}": "*"}${block ? ` and block "${block}"` : ""} (page through all results), read the other tags with rhino_get_user_text, then group and total them. Present a table, flag objects missing expected tags, and don't modify the document.`,
          },
        },
      ],
    }),
  );

  return server;
}

async function main(): Promise<void> {
  const bridge = new RhinoBridge();
  const server = createServer(bridge);
  const transport = new StdioServerTransport();
  const shutdown = () => {
    bridge.close();
    process.exit(0);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
  process.stdin.on("close", shutdown);
  await server.connect(transport);
}

main().catch((err) => {
  process.stderr.write(`open-rhino-mcp failed to start: ${err instanceof Error ? err.stack : String(err)}\n`);
  process.exit(1);
});
