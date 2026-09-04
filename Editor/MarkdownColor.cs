using System.Text.RegularExpressions;

namespace AIUnityMCPServer
{
    /// <summary>
    /// </summary>
    public static class MarkdownColor
    {
        const string CODE   = "#A5B4FC";
        const string STRONG = "#F4F6FA";
        const string HEADER = "#A99BFF";
        const string BULLET = "#9AA0AD"; // cool muted — bullet

        public static string ToRichText(string md)
        {
            if (string.IsNullOrEmpty(md)) return md;

            for (int ci = 0; ci < 20; ci++)
                md = md.Replace(((char)(0x2460 + ci)).ToString(), $"{ci + 1}.");

            md = md.Replace("<", "«").Replace(">", "»");

            md = Regex.Replace(md, @"([^\n])\n(#{1,6}\s)", "$1\n\n$2");

            // `inline code` → clay-peach
            md = Regex.Replace(md, "`([^`]+)`", $"<color={CODE}>$1</color>");

            md = Regex.Replace(md, @"\*\*([^*]+)\*\*", $"<b><color={STRONG}>$1</color></b>");

            md = Regex.Replace(md, @"(?m)^\s*#{1,6}\s*(.+)$", $"<size=14><b><color={HEADER}>$1</color></b></size>");

            md = Regex.Replace(md, @"(?m)^(\s*)[-*]\s+", $"$1<color={BULLET}>•</color> ");

            md = Regex.Replace(md, @"(?<![\w/>])(\w+\.cs)\b", $"<color={CODE}>$1</color>");

            md = Regex.Replace(md, @"(?m)^\s*---\s*$", "");
            md = Regex.Replace(md, @"\n{3,}", "\n\n");
            md = md.Trim();

            md = Regex.Replace(md, @"\n(?=<b><color=)", "\n<size=5> </size>\n");
            md = Regex.Replace(md, @"\n(?=<size=14>)", "\n<size=9> </size>\n");

            md = Regex.Replace(md, @"(?m)^⏱ .+$", m => $"<size=9><color=#5C6370>{m.Value}</color></size>");

            return "<color=#EEF0F4>" + md + "</color>";
        }
    }
}
