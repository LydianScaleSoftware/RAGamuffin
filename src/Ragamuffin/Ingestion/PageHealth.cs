using Ragamuffin.Data;

namespace Ragamuffin.Ingestion;

/// <summary>Where a PDF page stands, from the reader's point of view.</summary>
public enum PageState
{
    /// <summary>Has machine-read text nobody has checked; nothing points to a fault.</summary>
    Unverified,
    /// <summary>Claude checked it against the image, or the user marked it correct.</summary>
    Verified,
    /// <summary>No text, and the page image is blank too.</summary>
    Blank,
    /// <summary>Something was skipped or unusual, but the text is intact.</summary>
    Note,
    /// <summary>The text is probably wrong or missing.</summary>
    Problem,
    /// <summary>A problem or note the user muted.</summary>
    Muted,
}

/// <summary>
/// Sorts each PDF page into one <see cref="PageState"/> and keeps the document's counts of them, so a document whose
/// pages are fine shows as healthy instead of carrying warnings that can never be cleared.
/// </summary>
public static class PageHealth
{
    public const string GarbledReason = "Text looks garbled";

    public static bool IsVerified(DocumentPage p) => p.Source == PageSource.Claude || p.VerifiedAt is not null;

    /// <summary>The page's embedded text (still in use) looks like OCR noise.</summary>
    public static bool IsGarbled(DocumentPage p) =>
        p.SuspectReason == GarbledReason && p.Source is PageSource.Embedded or PageSource.Layout;

    /// <summary>What's wrong with the page's text, or null when nothing is (notes aren't problems).</summary>
    public static string? ProblemOf(DocumentPage p)
    {
        if (p.Issue is not null && p.IssueKind != IssueKinds.Note) return p.Issue;
        if (!PdfPages.HasUsefulText(p.EffectiveText) && p.Blank != true)
            return p.Blank == false ? "No text, but the page image has content." : "No text on this page.";
        if (IsGarbled(p)) return "The text looks garbled (like OCR noise).";
        return null;
    }

    public static PageState StateOf(DocumentPage p)
    {
        if (!PdfPages.HasUsefulText(p.EffectiveText) && p.Blank == true) return PageState.Blank;
        if (IsVerified(p)) return PageState.Verified;
        if (ProblemOf(p) is not null) return p.IssueMuted ? PageState.Muted : PageState.Problem;
        if (p.Issue is not null) return p.IssueMuted ? PageState.Muted : PageState.Note;
        return PageState.Unverified;
    }

    /// <summary>Records what went wrong (or what to note) on a page's latest run; a new issue is never muted.</summary>
    public static void SetIssue(DocumentPage p, string? issue, string kind = IssueKinds.Problem)
    {
        if (p.Issue != issue) p.IssueMuted = false;
        p.Issue = issue;
        p.IssueKind = issue is null ? null : kind;
    }

    /// <summary>Call when the page's text in use changes: a user's "correct" mark referred to the old text.</summary>
    public static void TextChanged(DocumentPage p) => p.VerifiedAt = null;

    public static void Apply(Document doc, IReadOnlyCollection<DocumentPage> pages)
    {
        var states = pages.Select(StateOf).ToList();
        doc.ProblemPages = states.Count(s => s == PageState.Problem);
        doc.NotePages = states.Count(s => s == PageState.Note);
        doc.MutedPages = states.Count(s => s == PageState.Muted);
        doc.VerifiedPages = states.Count(s => s == PageState.Verified);
        doc.BlankPages = states.Count(s => s == PageState.Blank);
    }

    /// <summary>"Verified", "N problems", "No problems": the one-line health the web UI and the MCP tools show.</summary>
    public static string Summary(Document doc) => Summary(doc.ProblemPages, doc.PageCount, doc.BlankPages, doc.VerifiedPages,
        doc.VerifiedAt, doc.VerifiedBy, doc.VerifiedNote);

    public static string Summary(int problemPages, int pageCount, int blankPages, int verifiedPages,
        DateTimeOffset? verifiedAt, string? verifiedBy, string? verifiedNote)
    {
        if (problemPages > 0) return $"{problemPages} problem page{(problemPages == 1 ? "" : "s")}";
        if (verifiedAt is not null) return verifiedBy == "user" ? "verified (marked correct by the user)" : $"verified by sample ({verifiedNote})";
        var textPages = pageCount - blankPages;
        if (textPages > 0 && verifiedPages >= textPages) return "verified (every page checked)";
        return "no problems (not verified)";
    }
}
