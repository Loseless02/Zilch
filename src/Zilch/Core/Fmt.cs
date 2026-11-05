namespace Zilch.Core
{
    public static class Fmt
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Size(long bytes)
        {
            if (bytes < 0) return "?";
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
            return u == 0 ? $"{bytes} B" : $"{v:0.0} {Units[u]}";
        }
    }
}
