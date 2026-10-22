import { build } from "esbuild";
import { chmodSync } from "node:fs";

await build({
  entryPoints: ["src/index.ts"],
  outfile: "dist/index.js",
  bundle: true,
  platform: "node",
  target: "node18",
  format: "esm",
  banner: {
    js: "#!/usr/bin/env node\nimport { createRequire as __cr } from 'module'; const require = __cr(import.meta.url);",
  },
  legalComments: "none",
  logLevel: "warning",
});
chmodSync("dist/index.js", 0o755);
console.log("bundled dist/index.js");
