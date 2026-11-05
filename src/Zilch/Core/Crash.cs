using System;

namespace Zilch.Core
{
    /// <summary>Errors Zilch survived. They land in the Logs tab like everything else.</summary>
    public static class Crash
    {
        public static void Log(Exception ex, string where = null)
        {
            if (ex == null) return;
            try
            {
                History.Add("Error", (where != null ? where + ": " : "") + ex.GetBaseException().Message,
                    ex.ToString().Replace("\r", "").Split('\n'));
            }
            catch { }
        }
    }
}
