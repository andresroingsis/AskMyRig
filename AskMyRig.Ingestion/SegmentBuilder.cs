namespace AskMyRig.Ingestion;

public static class SegmentBuilder
{
    /// <summary>
    /// Number of blocks classified as headings on the last run. Purely for
    /// diagnostics - if this is a large fraction of total blocks, the thresholds
    /// in LooksLikeHeading are too loose.
    /// </summary>
    public static int LastHeadingCount { get; private set; }

    public static List<Segment> Build(List<PageBlocks> pages, HashSet<string> boilerplate)
    {
        var bodySize = EstimateBodyFontSize(pages);
        var segments = new List<Segment>();
        string? currentHeading = null;

        LastHeadingCount = 0;

        foreach (var page in pages)
        {
            foreach (var block in page.Blocks)
            {
                if (boilerplate.Contains(TextCleaner.Normalise(block.Text)))
                {
                    continue;
                }

                if (TextCleaner.IsLetterSpaced(block.Text) || TextCleaner.IsTocEntry(block.Text))
                {
                    continue;
                }

                var isHeading = LooksLikeHeading(block, bodySize);

                if (isHeading)
                {
                    currentHeading = TextCleaner.Normalise(block.Text);
                    LastHeadingCount++;
                }

                // The block is added either way. A heading also marks a section
                // boundary, but its text is never thrown away.
                segments.Add(new Segment(block.Text, page.PageNumber, currentHeading, isHeading));
            }
        }

        return segments;
    }

    /// <summary>
    /// Finds the font size that carries the most text in the document.
    ///
    /// This replaces the previous per-page median of block sizes, which was too
    /// easily skewed. A page full of small table cells and captions produced a
    /// low median, which made ordinary body text look oversized and therefore
    /// like a heading. Weighting by character count across the whole document
    /// finds the real body size, because body text is by far the most voluminous
    /// thing in a manual.
    /// </summary>
    private static double EstimateBodyFontSize(List<PageBlocks> pages)
    {
        var weights = new Dictionary<double, int>();

        foreach (var block in pages.SelectMany(page => page.Blocks))
        {
            // Round to the nearest half point so that near-identical sizes group.
            var bucket = Math.Round(block.FontSize * 2, MidpointRounding.AwayFromZero) / 2;
            weights[bucket] = weights.GetValueOrDefault(bucket) + block.Text.Length;
        }

        if (weights.Count == 0)
        {
            return 10;
        }

        return weights.MaxBy(entry => entry.Value).Key;
    }

    private static bool LooksLikeHeading(BlockInfo block, double bodySize)
    {
        var text = block.Text.Trim();

        // Page numbers, figure callouts, single letters.
        if (text.Length < 4)
        {
            return false;
        }

        // Mostly digits or symbols - not a section title.
        var letters = text.Count(char.IsLetter);
        if (letters < text.Length * 0.5)
        {
            return false;
        }

        // Section titles are short.
        if (text.Length > 90)
        {
            return false;
        }

        // Sentence punctuation means prose.
        if (text.EndsWith('.') || text.EndsWith(',') || text.EndsWith(';') || text.EndsWith(':'))
        {
            return false;
        }

        // A multi-line block of any length is a paragraph, not a title. Real
        // headings that wrap are short enough to survive this.
        if (text.Contains('\n') && text.Length > 60)
        {
            return false;
        }

        // The decisive test, and much stricter than before: the block must be
        // meaningfully larger than body text. Bold-at-body-size is no longer
        // enough, because Yamaha manuals bold a great deal of ordinary text.
        return block.FontSize >= bodySize * 1.20;
    }
}