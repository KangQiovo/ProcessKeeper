internal static class LegacyFixtureAllocation
{
    static LegacyFixtureAllocation() { AppDomain.MonitoringIsEnabled = true; }
    internal static long Total => AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
}
