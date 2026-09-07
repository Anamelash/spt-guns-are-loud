namespace GunsAreLoud.Client.Audio
{
    internal static class WarmupJobPriority
    {
        internal static int Calculate(bool body, bool analysisPrefix, bool activeGroup)
        {
            if (body) return activeGroup ? 0 : 2;
            if (analysisPrefix) return activeGroup ? 1 : 4;
            return activeGroup ? 3 : 5;
        }
    }
}
