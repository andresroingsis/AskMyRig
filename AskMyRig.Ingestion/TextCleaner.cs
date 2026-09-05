using AskMyRig.Core;
using System.Text.RegularExpressions;

namespace AskMyRig.Ingestion;

public static class TextCleaner
{
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Matches letter-spaced text such as the vertical side tabs in Yamaha
    // reference manuals, which extract as "V o i c e s".
    private static readonly Regex LetterSpaced = new(@"^(?:\S\s){3,}\S$", RegexOptions.Compiled);

    // Matches table-of-contents lines such as "Appendix...115".
    private static readonly Regex TocEntry = new(@"\.{3,}\s*\d+\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Collapses whitespace and replaces digit runs with '#', so that
    /// "PSR-SX920 Reference Manual 12" and "... 13" become the same key.
    /// </summary>
    public static string Normalise(string text)
    {
        var collapsed = Whitespace.Replace(text.Trim(), " ");
        return Digits.Replace(collapsed, "#");
    }

    public static bool IsLetterSpaced(string text) => LetterSpaced.IsMatch(text.Trim());

    public static bool IsTocEntry(string text) => TocEntry.IsMatch(text);

    /// <summary>
    /// Finds text repeating across most pages: running headers, footers and
    /// side tabs.
    ///
    /// The previous version only sampled the first and last two blocks of each
    /// page, on the assumption that headers and footers sort to the edges of the
    /// block list. On dense multi-column pages the reading-order detector places
    /// them in the middle, so nothing was ever caught. We now consider every
    /// short block on the page - long paragraphs are never boilerplate, so
    /// length is a safer filter than position.
    /// </summary>
    public static HashSet<string> FindBoilerplate(List<PageBlocks> pages, double threshold = 0.5)
    {
        var counts = new Dictionary<string, int>();

        foreach (var page in pages)
        {
            var keys = page.Blocks
                .Where(block => block.Text.Length <= 120)
                .Select(block => Normalise(block.Text))
                .Where(key => key.Length >= 3)
                .Distinct();

            foreach (var key in keys)
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        // Require an absolute floor as well as a fraction, so that short
        // documents do not flag ordinary text that happens to appear twice.
        if (pages.Count < 10) return [];
        var minimum = Math.Max(5, pages.Count * threshold);

        return counts
            .Where(entry => entry.Value >= minimum)
            .Select(entry => entry.Key)
            .ToHashSet();
    }
}