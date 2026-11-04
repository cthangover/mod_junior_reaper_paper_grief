namespace Cthangover.Mods.JuniorReaper.Tests.Lamp
{
    using Cthangover.Core.Mods;
    /// <summary>Tests the real shared toolbar control only when its explicitly requested suite is mounted.</summary>
    public class StasLampTestInitializer : IModInitializer
    {
        private bool added;
        public void OnModLoaded(string modId) { }
        public void OnModResourcesReady()
        {
            if (added) return;
            added = true;
            ModRoot.AddDeferred(new StasLampTestDriver());
        }
    }
}
