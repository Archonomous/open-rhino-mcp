using System.Runtime.InteropServices;
using OpenRhinoMcp.Bridge;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace OpenRhinoMcp.Commands
{
    [Guid("c676d106-cb50-42e0-98ca-7e8cd474eae3")]
    public class OpenRhinoMcpStartCommand : Command
    {
        public override string EnglishName => "OpenRhinoMcpStart";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            OpenRhinoMcpPlugin.Instance.StartServer();
            return Result.Success;
        }
    }

    [Guid("5995724f-f71d-4016-a389-5301206fe077")]
    public class OpenRhinoMcpStopCommand : Command
    {
        public override string EnglishName => "OpenRhinoMcpStop";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            OpenRhinoMcpPlugin.Instance.StopServer();
            return Result.Success;
        }
    }

    [Guid("0846d9d3-8d75-4874-a332-3c1c80ce21fa")]
    public class OpenRhinoMcpStatusCommand : Command
    {
        public override string EnglishName => "OpenRhinoMcpStatus";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = OpenRhinoMcpPlugin.Instance;
            var server = plugin.Server;
            if (server == null || !server.IsRunning)
            {
                RhinoApp.WriteLine("Open Rhino MCP {0}: stopped. Run OpenRhinoMcpStart to start it.", plugin.Version);
            }
            else
            {
                RhinoApp.WriteLine(string.Format("Open Rhino MCP {0}: listening on 127.0.0.1:{1}, {2} client(s) connected, {3} request(s) served.",
                    plugin.Version, server.Port, server.ClientCount, server.RequestCount));
                RhinoApp.WriteLine("Session file: {0}", Discovery.SessionFilePath);
            }
            RhinoApp.WriteLine("Auto-start with Rhino: {0}. Preferred port: {1}.", plugin.AutoStart ? "on" : "off", plugin.PreferredPort);
            return Result.Success;
        }
    }

    [Guid("dedeafb2-b433-4fde-a11e-5acf9bfdd0b4")]
    public class OpenRhinoMcpSettingsCommand : Command
    {
        public override string EnglishName => "OpenRhinoMcpSettings";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = OpenRhinoMcpPlugin.Instance;
            var autoStart = new OptionToggle(plugin.AutoStart, "Off", "On");
            var port = new OptionInteger(plugin.PreferredPort, 1024, 65535);

            var go = new GetOption();
            go.SetCommandPrompt("Open Rhino MCP settings");
            go.AcceptNothing(true);
            while (true)
            {
                go.ClearCommandOptions();
                go.AddOptionToggle("AutoStart", ref autoStart);
                go.AddOptionInteger("Port", ref port);
                var res = go.Get();
                if (res == GetResult.Option) continue;
                if (res == GetResult.Nothing) break;
                return Result.Cancel;
            }

            plugin.AutoStart = autoStart.CurrentValue;
            if (plugin.PreferredPort != port.CurrentValue)
            {
                plugin.PreferredPort = port.CurrentValue;
                RhinoApp.WriteLine("Port changed. Run OpenRhinoMcpStop then OpenRhinoMcpStart to apply.");
            }
            return Result.Success;
        }
    }
}
