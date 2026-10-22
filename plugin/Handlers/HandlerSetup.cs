namespace OpenRhinoMcp.Handlers
{
    public static class HandlerSetup
    {
        static bool _done;

        public static void RegisterAll()
        {
            if (_done) return;
            SystemHandlers.Register();
            CommandHandlers.Register();
            PythonHandlers.Register();
            ApiHandlers.Register();
            DocHandlers.Register();
            LayerHandlers.Register();
            ObjectHandlers.Register();
            UserTextHandlers.Register();
            GeometryHandlers.Register();
            BlockHandlers.Register();
            AnnotationHandlers.Register();
            ViewHandlers.Register();
            _done = true;
        }
    }
}
