using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eleon.Modding;

namespace ShipWalk
{
    public sealed class ShipWalkMod : IMod
    {
        private IModApi api;
        private Runtime runtime;
        private TravelHub hub;
        private string folder;
        private string initializationError;

        public void Init(IModApi modApi)
        {
            api = modApi;
            folder = Path.GetDirectoryName(typeof(ShipWalkMod).Assembly.Location);
            try
            {
                if (api.Application.Mode == ApplicationMode.DedicatedServer)
                {
                    hub = new TravelHub(api); api.Application.Update += Update; return;
                }
                AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
                Assembly.LoadFrom(Path.Combine(folder, "0Harmony.dll"));
                StartRuntime();
                api.Application.Update += Update;
                api.Application.FixedUpdate += BeforePhysics;
                api.Application.GameEntered += GameEntered;
            }
            catch (Exception error)
            {
                Exception cause = error.GetBaseException();
                initializationError = cause.GetType().Name + ": " + cause.Message;
                try { runtime?.Dispose(); }
                catch (Exception cleanupError) { initializationError += "; cleanup: " + cleanupError.GetBaseException().Message; }
                runtime = null;
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StartRuntime()
        {
            runtime = new Runtime(api, folder);
            runtime.Install();
        }

        private Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            return new AssemblyName(args.Name).Name == "0Harmony"
                ? Assembly.LoadFrom(Path.Combine(folder, "0Harmony.dll")) : null;
        }

        private void Update()
        {
            try { if (hub != null) hub.Update(); else runtime?.Update(); }
            catch (Exception error) { if (hub != null) initializationError = error.GetBaseException().ToString(); else runtime?.Fail(error); }
        }

        private void GameEntered(bool entered) => runtime?.Reset(entered ? "game-entered" : "game-left");

        private void BeforePhysics()
        {
            try { runtime?.BeforePhysics(); }
            catch (Exception error) { runtime?.Fail(error); }
        }

        // Console: mod exs <command>, or mod ex <mod-num> <command> after mod list.
        public void ExecCommand(List<string> args)
        {
            try
            {
                if (hub != null) { api.Log("[ShipWalk] Dedicated travel coordinator loaded. " + hub.Status + "; " + initializationError); return; }
                if (runtime == null) { api?.LogWarning("[ShipWalk] Not loaded. " + initializationError); return; }
                runtime.Command(args);
            }
            catch (Exception error) { runtime?.Fail(error); }
        }

        public void Shutdown()
        {
            if (api != null)
            {
                api.Application.Update -= Update;
                api.Application.FixedUpdate -= BeforePhysics;
                api.Application.GameEntered -= GameEntered;
            }
            runtime?.Dispose();
            runtime = null;
            hub?.Shutdown(); hub = null;
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
        }
    }
}
