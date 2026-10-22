using System;
using OpenRhinoMcp.Bridge;
using OpenRhinoMcp.Handlers;
using Rhino;
using Rhino.PlugIns;

namespace OpenRhinoMcp
{
    /// <summary>
    /// Loads with Rhino, registers bridge methods and (by default) starts the local bridge server
    /// so MCP clients can connect without the user typing anything.
    /// </summary>
    public class OpenRhinoMcpPlugin : PlugIn
    {
        public const int DefaultPort = 47820;
        public static OpenRhinoMcpPlugin Instance { get; private set; }

        public BridgeServer Server { get; private set; }

        public OpenRhinoMcpPlugin()
        {
            Instance = this;
        }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        public bool AutoStart
        {
            get => Settings.GetBool("AutoStart", true);
            set => Settings.SetBool("AutoStart", value);
        }

        public int PreferredPort
        {
            get => Settings.GetInteger("Port", DefaultPort);
            set => Settings.SetInteger("Port", value);
        }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            try
            {
                HandlerSetup.RegisterAll();
                if (AutoStart)
                    StartServer(quiet: true);
            }
            catch (Exception ex)
            {
                // Never block Rhino from starting; report and stay loaded so the user can retry.
                RhinoApp.WriteLine("Open Rhino MCP: failed to start bridge: " + ex.Message);
            }
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            StopServer(quiet: true);
            base.OnShutdown();
        }

        public bool StartServer(bool quiet = false)
        {
            if (Server != null && Server.IsRunning)
            {
                if (!quiet) RhinoApp.WriteLine("Open Rhino MCP is already running on 127.0.0.1:{0}.", Server.Port);
                return true;
            }
            Server = new BridgeServer();
            Server.Start(PreferredPort, Version);
            RhinoApp.WriteLine("Open Rhino MCP listening on 127.0.0.1:{0}.", Server.Port);
            return true;
        }

        public void StopServer(bool quiet = false)
        {
            if (Server == null) return;
            Server.Stop();
            Server = null;
            if (!quiet) RhinoApp.WriteLine("Open Rhino MCP stopped.");
        }
    }
}
