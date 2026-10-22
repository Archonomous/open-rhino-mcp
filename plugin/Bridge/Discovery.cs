using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;

namespace OpenRhinoMcp.Bridge
{
    /// <summary>
    /// Writes the session file the MCP server reads to find the port and token.
    /// Windows: %APPDATA%\OpenRhinoMCP\session.json. macOS: ~/Library/Application Support/OpenRhinoMCP/session.json.
    /// </summary>
    public static class Discovery
    {
        public static string SessionFilePath
        {
            get
            {
                string root;
                if (Rhino.Runtime.HostUtils.RunningOnOSX)
                {
                    var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                    root = Path.Combine(home, "Library", "Application Support");
                }
                else
                {
                    root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                return Path.Combine(root, "OpenRhinoMCP", "session.json");
            }
        }

        public static void Write(int port, string token, string pluginVersion, int protocol)
        {
            try
            {
                var path = SessionFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var json = new JObject
                {
                    ["port"] = port,
                    ["token"] = token,
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["rhino_version"] = RhinoApp.Version.ToString(),
                    ["plugin_version"] = pluginVersion,
                    ["protocol"] = protocol,
                    ["started_at"] = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(path, json.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Open Rhino MCP: couldn't write session file: " + ex.Message);
            }
        }

        /// <summary>Deletes the session file only if it still belongs to this Rhino (another Rhino may have taken over).</summary>
        public static void Delete(string token)
        {
            try
            {
                var path = SessionFilePath;
                if (!File.Exists(path)) return;
                var current = JObject.Parse(File.ReadAllText(path));
                if (current.Value<string>("token") == token) File.Delete(path);
            }
            catch { }
        }
    }
}
