namespace VoiceFlow.Core.Library;

/// <summary>A Scratchpad note. Field names match the macOS app's notes.</summary>
public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    public bool Pinned { get; set; }

    public Note() { }

    public Note(string body) => Body = body;

    /// <summary>The title shown in lists: the saved title, else the first words of the note.</summary>
    public string DisplayTitle
    {
        get
        {
            var title = Title.Trim();
            if (title.Length > 0) return title;
            var words = Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(6);
            var joined = string.Join(' ', words);
            return joined.Length == 0 ? "Untitled" : joined;
        }
    }

    public bool IsBlank => Title.Trim().Length == 0 && Body.Trim().Length == 0;
}
