using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Zilch.Core;

namespace Zilch.UI
{
    public enum BarSize { Small, Medium, Large }
    public enum BarPlace { Top, Bottom }
    public enum ShowKind { All, FoldersOnly, FilesOnly }

    /// <summary>
    /// The Settings tab. Saved as plain key=value lines in %LOCALAPPDATA%\Zilch\settings.ini.
    /// </summary>
    public static class Prefs
    {
        static readonly string FilePath = Path.Combine(History.Folder, "settings.ini");

        // storage bar
        public static bool BarVisible = true;
        public static BarSize BarHeight = BarSize.Medium;
        public static BarPlace BarPosition = BarPlace.Top;
        public static bool BarLegend = true;
        public static bool BarLegendSizes = true;
        public static bool BarPercent;
        public static bool BarFree = true;
        public static bool BarHidden = true;

        // colors
        public static bool ColorChips = true;      // little category square before each name
        public static bool ColorShareBars = true;  // share bar in the category color (else plain blue)
        public static bool ColorRisk = true;       // orange / red / gray text for shared and system items
        public static bool BoldFolders = true;

        // details (columns in Space and Inspector)
        public static bool ColShare = true, ColFiles = true, ColKind = true, ColNote = true;

        // filters (Space and Inspector lists)
        public static ShowKind Show = ShowKind.All;
        public static long MinBytes;               // 0 = no minimum

        public static event Action Changed;

        static readonly Color[] DefaultCat = (Color[])Theme.CatColors.Clone();

        public static void Apply() { Save(); try { Changed?.Invoke(); } catch (Exception ex) { Crash.Log(ex, "settings"); } }

        public static void Reset()
        {
            BarVisible = true; BarHeight = BarSize.Medium; BarPosition = BarPlace.Top; BarLegend = true; BarLegendSizes = true;
            BarPercent = false; BarFree = true; BarHidden = true;
            ColorChips = true; ColorShareBars = true; ColorRisk = true; BoldFolders = true;
            ColShare = ColFiles = ColKind = ColNote = true;
            Show = ShowKind.All; MinBytes = 0;
            DefaultCat.CopyTo(Theme.CatColors, 0);
            Apply();
        }

        public static bool FilterActive => Show != ShowKind.All || MinBytes > 0;

        /// <summary>Does a row pass the filter?</summary>
        public static bool Pass(bool isDir, long size) =>
            (Show == ShowKind.All || (Show == ShowKind.FoldersOnly) == isDir) && size >= MinBytes;

        public static string FilterText()
        {
            var parts = new List<string>();
            if (Show == ShowKind.FoldersOnly) parts.Add("folders only");
            if (Show == ShowKind.FilesOnly) parts.Add("files only");
            if (MinBytes > 0) parts.Add("at least " + Fmt.Size(MinBytes));
            return string.Join(", ", parts);
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim(); var v = line.Substring(eq + 1).Trim();
                    bool B() => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    switch (k)
                    {
                        case "bar": BarVisible = B(); break;
                        case "bar.size": Enum.TryParse(v, out BarHeight); break;
                        case "bar.position": Enum.TryParse(v, out BarPosition); break;
                        case "bar.legend": BarLegend = B(); break;
                        case "bar.legend.sizes": BarLegendSizes = B(); break;
                        case "bar.percent": BarPercent = B(); break;
                        case "bar.free": BarFree = B(); break;
                        case "bar.hidden": BarHidden = B(); break;
                        case "color.chips": ColorChips = B(); break;
                        case "color.sharebars": ColorShareBars = B(); break;
                        case "color.risk": ColorRisk = B(); break;
                        case "bold.folders": BoldFolders = B(); break;
                        case "col.share": ColShare = B(); break;
                        case "col.files": ColFiles = B(); break;
                        case "col.kind": ColKind = B(); break;
                        case "col.note": ColNote = B(); break;
                        case "filter.show": Enum.TryParse(v, out Show); break;
                        case "filter.min": long.TryParse(v, out MinBytes); break;
                        default:
                            if (k.StartsWith("cat.") && int.TryParse(k.Substring(4), out int i) && i >= 0 && i < Theme.CatColors.Length &&
                                int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                                Theme.CatColors[i] = Color.FromArgb(255, Color.FromArgb(rgb));
                            break;
                    }
                }
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                string b(bool x) => x ? "1" : "0";
                var lines = new List<string>
                {
                    "# Zilch settings. Delete this file to go back to defaults.",
                    "bar=" + b(BarVisible), "bar.size=" + BarHeight, "bar.position=" + BarPosition, "bar.legend=" + b(BarLegend),
                    "bar.legend.sizes=" + b(BarLegendSizes), "bar.percent=" + b(BarPercent), "bar.free=" + b(BarFree), "bar.hidden=" + b(BarHidden),
                    "color.chips=" + b(ColorChips), "color.sharebars=" + b(ColorShareBars), "color.risk=" + b(ColorRisk), "bold.folders=" + b(BoldFolders),
                    "col.share=" + b(ColShare), "col.files=" + b(ColFiles), "col.kind=" + b(ColKind), "col.note=" + b(ColNote),
                    "filter.show=" + Show, "filter.min=" + MinBytes,
                };
                for (int i = 0; i < Theme.CatColors.Length; i++)
                    if (Theme.CatColors[i].ToArgb() != DefaultCat[i].ToArgb())
                        lines.Add($"cat.{i}={Theme.CatColors[i].ToArgb() & 0xFFFFFF:X6}");
                Directory.CreateDirectory(History.Folder);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }
    }
}
