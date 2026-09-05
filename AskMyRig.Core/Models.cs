namespace AskMyRig.Core;

/// <summary>
/// A rectangular run of text found on a page, together with the font
/// information we need in order to tell headings apart from body text.
/// </summary>
public record BlockInfo(string Text, double FontSize, bool IsBold);

/// <summary>
/// Every text block found on a single page, already sorted into reading order.
/// </summary>
public record PageBlocks(int PageNumber, List<BlockInfo> Blocks);

/// <summary>
/// A block of text after boilerplate has been stripped.
///
/// Heading blocks are kept as segments rather than discarded. This is
/// deliberate: heading detection is a heuristic and will get things wrong, and
/// dropping a misidentified block loses real content permanently. Keeping it
/// costs one slightly odd-looking line. StartsSection marks it as a boundary so
/// the chunker knows to break there.
/// </summary>
public record Segment(string Text, int Page, string? Heading, bool StartsSection);

/// <summary>
/// The final unit of retrieval. One chunk becomes one embedding and one row in
/// the Chunks table. Content already has the heading prepended.
/// </summary>
public record Chunk(
    string Manual,
    int PageFrom,
    int PageTo,
    string? Heading,
    string Content,
    int Tokens);