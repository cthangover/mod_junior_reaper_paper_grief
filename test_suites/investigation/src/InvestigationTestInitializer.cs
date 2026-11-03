namespace Cthangover.Mods.JuniorReaper.Tests
{
    using System;
    using Cthangover.Core.Mods;
    using Godot;

    /// <summary>Mounts the public-contract play test only for its explicit launch flag.</summary>
    public class InvestigationTestInitializer : IModInitializer
    {
        private static bool queued;

        /// <inheritdoc />
        public void OnModLoaded(string id) { }

        /// <inheritdoc />
        public void OnModResourcesReady()
        {
            if (queued) return;
            var active = false;
            string steps = null;
            string shots = null;
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--itest=jrp_investigation") active = true;
                if (arg.StartsWith("--investigation-steps=", StringComparison.Ordinal)) steps = arg.Substring(22);
                if (arg.StartsWith("--shots=", StringComparison.Ordinal)) shots = arg.Substring(8);
            }
            if (!active) return;
            queued = true;
            ModRoot.AddDeferred(new InvestigationTestDriver(steps, shots));
        }
    }
}
