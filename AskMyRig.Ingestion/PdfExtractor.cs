using AskMyRig.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

namespace AskMyRig.Ingestion;

public static class PdfExtractor
{
    /// <summary>
    /// Opens a PDF and returns its text as blocks, page by page.
    ///
    /// Important: PdfPig Page objects must never leave this method. They hold a
    /// reference to the document's internal reader, so once the 'using' disposes
    /// the document they become unreliable - sometimes working, sometimes
    /// throwing, depending on what was already loaded into memory. We do all the
    /// reading here and return plain records instead.
    /// </summary>
    public static List<PageBlocks> Extract(string path)
    {
        using var document = PdfDocument.Open(path);

        var pages = new List<PageBlocks>();

        foreach (var page in document.GetPages())
        {
            // Words carry position and font data for every glyph on the page.
            var words = page.GetWords();

            // Docstrum groups words into blocks based on how close they sit to
            // each other. This is what makes two-column layouts come out right -
            // it recognises the columns as separate regions instead of reading
            // straight across the page.
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

            // Then we sort those blocks into the order a human would read them.
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);

            var infos = new List<BlockInfo>();

            foreach (var block in ordered)
            {
                var text = block.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                // Drill down to individual letters to read font size and weight.
                // Block -> lines -> words -> letters.
                var letters = block.TextLines
                    .SelectMany(line => line.Words)
                    .SelectMany(word => word.Letters)
                    .ToList();

                if (letters.Count == 0)
                {
                    continue;
                }

                var averageSize = letters.Average(letter => letter.PointSize);

                // Treat the block as bold only if most of its letters are bold.
                // A single bold word inside a paragraph should not count.
                var boldLetters = letters.Count(letter => letter.Font.IsBold);
                var isBold = boldLetters > letters.Count / 2;

                infos.Add(new BlockInfo(text, averageSize, isBold));
            }

            pages.Add(new PageBlocks(page.Number, infos));
        }

        return pages;
    }
}
