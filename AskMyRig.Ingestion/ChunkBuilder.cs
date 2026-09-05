using AskMyRig.Core;
using Microsoft.ML.Tokenizers;
using System.Text;
using System.Text.RegularExpressions;

namespace AskMyRig.Ingestion;

public static class ChunkBuilder
{
    private static readonly Regex SentenceBoundary = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    /// <summary>
    /// Packs segments into chunks, breaking at section boundaries.
    ///
    /// The key change from the first version: a chunk now ends when the heading
    /// changes, not only when it fills up. Previously a single chunk could span
    /// three unrelated topics and carry the heading of only the first, which
    /// mislabelled most of its content and made retrieval unreliable.
    /// </summary>
    /// <param name="minTokensForSectionBreak">
    /// A section boundary only closes a chunk if the chunk already has this much
    /// content. Without it, a run of consecutive sub-headings would produce a
    /// stream of near-empty chunks.
    /// </param>
    public static List<Chunk> Build(
        string manualName,
        List<Segment> segments,
        TiktokenTokenizer tokenizer,
        int maxTokens = 600,
        int overlapTokens = 100,
        int minTokensForSectionBreak = 120)
    {
        var chunks = new List<Chunk>();
        var current = new List<Segment>();
        var currentTokens = 0;

        foreach (var rawSegment in segments)
        {
            foreach (var segment in SplitOversized(rawSegment, tokenizer, maxTokens))
            {
                var tokens = tokenizer.CountTokens(segment.Text);

                // A new section starts here: close the current chunk cleanly.
                // No overlap is carried across a section boundary - the previous
                // section's tail is not useful context for the new one.
                if (segment.StartsSection && currentTokens >= minTokensForSectionBreak)
                {
                    chunks.Add(Materialise(manualName, current, tokenizer));
                    current = [];
                    currentTokens = 0;
                }
                else if (currentTokens + tokens > maxTokens && current.Count > 0)
                {
                    chunks.Add(Materialise(manualName, current, tokenizer));
                    current = TakeOverlap(current, tokenizer, overlapTokens);
                    currentTokens = current.Sum(s => tokenizer.CountTokens(s.Text));
                }

                current.Add(segment);
                currentTokens += tokens;
            }
        }

        if (current.Count > 0)
        {
            chunks.Add(Materialise(manualName, current, tokenizer));
        }

        return chunks;
    }

    private static List<Segment> TakeOverlap(
        List<Segment> closedChunk,
        TiktokenTokenizer tokenizer,
        int overlapTokens)
    {
        var carry = new List<Segment>();
        var carried = 0;

        for (var i = closedChunk.Count - 1; i >= 0; i--)
        {
            var tokens = tokenizer.CountTokens(closedChunk[i].Text);

            if (carried + tokens > overlapTokens)
            {
                break;
            }

            carry.Insert(0, closedChunk[i]);
            carried += tokens;
        }

        return carry;
    }

    private static Chunk Materialise(
        string manualName,
        List<Segment> segments,
        TiktokenTokenizer tokenizer)
    {
        var heading = segments
            .Select(segment => segment.Heading)
            .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));

        var body = string.Join("\n\n", segments.Select(segment => segment.Text));

        // Only prepend the heading if the body does not already open with it,
        // which it now often will, since heading blocks are kept in the stream.
        var content = heading is null || body.StartsWith(heading, StringComparison.Ordinal)
            ? body
            : $"{heading}\n\n{body}";

        return new Chunk(
            manualName,
            segments.Min(segment => segment.Page),
            segments.Max(segment => segment.Page),
            heading,
            content,
            tokenizer.CountTokens(content));
    }

    private static IEnumerable<Segment> SplitOversized(
        Segment segment,
        TiktokenTokenizer tokenizer,
        int maxTokens)
    {
        if (tokenizer.CountTokens(segment.Text) <= maxTokens)
        {
            yield return segment;
            yield break;
        }

        var buffer = new StringBuilder();
        var bufferTokens = 0;
        var first = true;

        foreach (var sentence in SentenceBoundary.Split(segment.Text))
        {
            var tokens = tokenizer.CountTokens(sentence);

            if (bufferTokens + tokens > maxTokens && buffer.Length > 0)
            {
                // Only the first piece inherits StartsSection, so an oversized
                // heading block does not trigger a break on every fragment.
                yield return segment with { Text = buffer.ToString().Trim(), StartsSection = first };
                first = false;
                buffer.Clear();
                bufferTokens = 0;
            }

            buffer.Append(sentence).Append(' ');
            bufferTokens += tokens;
        }

        if (buffer.Length > 0)
        {
            yield return segment with { Text = buffer.ToString().Trim(), StartsSection = first };
        }
    }
}