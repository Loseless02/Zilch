using System;

namespace Zilch.Core
{
    public enum Mode { Simple, Advanced, Sudo }

    /// <summary>Global UI state shared by every tab.</summary>
    public static class AppState
    {
        static Mode _mode = Mode.Simple;
        static bool _recycle;

        public static Mode Mode
        {
            get => _mode;
            set { if (_mode == value) return; _mode = value; Changed?.Invoke(); }
        }

        /// <summary>Send deletions to the Recycle Bin instead of wiping them. Off by default: the point is to free space.</summary>
        public static bool UseRecycleBin
        {
            get => _recycle;
            set { if (_recycle == value) return; _recycle = value; Changed?.Invoke(); }
        }

        public static bool IsAdvancedOrMore => _mode != Mode.Simple;
        public static bool IsSudo => _mode == Mode.Sudo;

        public static event Action Changed;
    }
}
