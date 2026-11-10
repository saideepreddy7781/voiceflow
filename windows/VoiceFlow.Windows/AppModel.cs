using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Windows.Native;

namespace VoiceFlow.Windows;

/// <summary>
/// The one shared app state: preferences, the library (dictionary, snippets, history) and the API keys.
/// Lives on the UI thread. Files are JSON in %APPDATA%\VoiceFlow, written atomically.
/// </summary>
internal sealed class AppModel
{
    readonly PreferenceStore _preferenceStore;
    readonly JsonFile<List<DictionaryEntry>> _dictionaryFile;
    readonly JsonFile<List<Snippet>> _snippetsFile;
    readonly JsonFile<List<HistoryEntry>> _historyFile;
    readonly JsonFile<List<Note>> _notesFile;
    readonly CredentialStore _secrets;
    readonly Dictionary<SecretKey, string> _cachedKeys = new();
    readonly HashSet<SecretKey> _storedKeys = new();

    public string DataFolder { get; }
    public Preferences Preferences { get; private set; }
    public List<DictionaryEntry> Dictionary { get; private set; }
    public List<Snippet> Snippets { get; private set; }
    public List<HistoryEntry> History { get; private set; }
    public List<Note> Notes { get; private set; }
    public Dictionary<SecretKey, KeyCheckResult> KeyChecks { get; } = new();

    /// <summary>Old and new preferences, after the change is saved.</summary>
    public event Action<Preferences, Preferences>? PreferencesChanged;
    public event Action? LibraryChanged;
    public event Action? NotesChanged;
    public event Action? KeysChanged;

    /// <param name="credentialPrefix">Credential Manager target prefix; the self-test uses its own so it never touches real keys.</param>
    public AppModel(string dataFolder, string credentialPrefix = "VoiceFlow/")
    {
        DataFolder = dataFolder;
        _secrets = new CredentialStore(credentialPrefix);
        _preferenceStore = new PreferenceStore(dataFolder);
        _dictionaryFile = new JsonFile<List<DictionaryEntry>>("dictionary.json", dataFolder);
        _snippetsFile = new JsonFile<List<Snippet>>("snippets.json", dataFolder);
        _historyFile = new JsonFile<List<HistoryEntry>>("history.json", dataFolder);
        _notesFile = new JsonFile<List<Note>>("notes.json", dataFolder);
        Preferences = _preferenceStore.Load();
        Dictionary = _dictionaryFile.Load() ?? [];
        Snippets = _snippetsFile.Load() ?? [];
        Notes = _notesFile.Load() ?? [];
        History = HistoryRetention.Apply(_historyFile.Load() ?? [], Preferences.HistoryRetentionDays);
        SaveHistory();
        RefreshStoredKeys();
    }

    // MARK: Preferences

    public void UpdatePreferences(Action<Preferences> change)
    {
        var old = Preferences.Clone();
        var updated = Preferences.Clone();
        change(updated);
        if (updated.Equals(old)) return;
        Preferences = updated;
        try { _preferenceStore.Save(updated); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Logger.Error("Settings could not be saved", e); }
        if (updated.HistoryRetentionDays != old.HistoryRetentionDays)
        {
            History = HistoryRetention.Apply(History, updated.HistoryRetentionDays);
            SaveHistory();
            LibraryChanged?.Invoke();
        }
        Logger.DebugEnabled = updated.DebugLogging;
        PreferencesChanged?.Invoke(old, updated);
    }

    // MARK: History

    public void Record(HistoryEntry entry)
    {
        if (Preferences.HistoryRetentionDays == 0) return;
        History.Insert(0, entry);
        SaveHistory();
        LibraryChanged?.Invoke();
    }

    public void DeleteHistory(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        History.RemoveAll(e => set.Contains(e.Id));
        SaveHistory();
        LibraryChanged?.Invoke();
    }

    public void ToggleHistoryFlag(Guid id)
    {
        var entry = History.FirstOrDefault(e => e.Id == id);
        if (entry == null) return;
        entry.Flagged = entry.Flagged != true;
        SaveHistory();
        LibraryChanged?.Invoke();
    }

    public void ClearHistory()
    {
        History.Clear();
        SaveHistory();
        LibraryChanged?.Invoke();
    }

    /// <summary>Drops entries past the retention period (called daily while the app runs).</summary>
    public void ApplyRetention()
    {
        var kept = HistoryRetention.Apply(History, Preferences.HistoryRetentionDays);
        if (kept.Count == History.Count) return;
        History = kept;
        SaveHistory();
        LibraryChanged?.Invoke();
    }

    public UsageStats Stats => UsageStats.Compute(History);

    public string? LastResult => History.FirstOrDefault(e => e.Final.Length > 0)?.Final;

    void SaveHistory() => Save(() => _historyFile.Save(History), "history");

    // MARK: Dictionary

    public List<string> DictionaryTerms => Dictionary.Select(e => e.Term).ToList();

    /// <summary>Adds a word or a correction. Returns an error message, or null when added.</summary>
    public string? AddDictionaryEntry(string term, string? misspelling = null, DictionarySource source = DictionarySource.Manual)
    {
        var m = string.IsNullOrWhiteSpace(misspelling) ? null : misspelling.Trim();
        if (DictionaryEntry.Validate(term, m, Dictionary) is { } error) return error;
        Dictionary.Insert(0, new DictionaryEntry(term.Trim(), m, source));
        SaveDictionary();
        return null;
    }

    /// <summary>Edits an entry. Returns an error message, or null when saved.</summary>
    public string? UpdateDictionaryEntry(Guid id, string term, string? misspelling)
    {
        var entry = Dictionary.FirstOrDefault(e => e.Id == id);
        if (entry == null) return null;
        var m = string.IsNullOrWhiteSpace(misspelling) ? null : misspelling.Trim();
        if (DictionaryEntry.Validate(term, m, Dictionary.Where(e => e.Id != id)) is { } error) return error;
        entry.Term = term.Trim();
        entry.Misspelling = m;
        SaveDictionary();
        return null;
    }

    /// <summary>Removes a word learned by mistake (the toast's Undo button).</summary>
    public void RemoveDictionaryTerm(string term)
    {
        if (Dictionary.RemoveAll(e => e.Term == term && e.Source == DictionarySource.AutoLearned) > 0) SaveDictionary();
    }

    public void RemoveDictionaryEntries(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        Dictionary.RemoveAll(e => set.Contains(e.Id));
        SaveDictionary();
    }

    void SaveDictionary()
    {
        Save(() => _dictionaryFile.Save(Dictionary), "dictionary");
        LibraryChanged?.Invoke();
    }

    // MARK: Snippets

    /// <summary>Adds or updates a snippet. Returns an error message, or null when saved.</summary>
    public string? SaveSnippet(string trigger, string expansion, Guid? editing = null)
    {
        if (SnippetMatcher.Validate(trigger, expansion, Snippets, editing) is { } error) return error;
        var existing = editing is { } id ? Snippets.FirstOrDefault(s => s.Id == id) : null;
        if (existing != null)
        {
            existing.Trigger = trigger.Trim();
            existing.Expansion = expansion;
        }
        else Snippets.Insert(0, new Snippet(trigger.Trim(), expansion));
        SaveSnippets();
        return null;
    }

    public void RemoveSnippets(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        Snippets.RemoveAll(s => set.Contains(s.Id));
        SaveSnippets();
    }

    void SaveSnippets()
    {
        Save(() => _snippetsFile.Save(Snippets), "snippets");
        LibraryChanged?.Invoke();
    }

    // MARK: Scratchpad

    public Note CreateNote()
    {
        var note = new Note();
        Notes.Insert(0, note);
        SaveNotes();
        NotesChanged?.Invoke();
        return note;
    }

    public void DeleteNote(Guid id)
    {
        if (Notes.RemoveAll(n => n.Id == id) == 0) return;
        SaveNotes();
        NotesChanged?.Invoke();
    }

    public void SaveNotes() => Save(() => _notesFile.Save(Notes), "notes");

    static void Save(Action write, string what)
    {
        try { write(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Logger.Error($"Could not save {what}", e); }
    }

    // MARK: Keys

    public bool HasKey(SecretKey key) => _storedKeys.Contains(key);

    /// <summary>The key, read once from Credential Manager and then kept in memory for this run.</summary>
    public string? GetKey(SecretKey key = SecretKey.Groq)
    {
        if (_cachedKeys.TryGetValue(key, out var cached)) return cached;
        if (!_storedKeys.Contains(key)) return null;
        try
        {
            var value = _secrets.Read(key);
            if (!string.IsNullOrEmpty(value)) _cachedKeys[key] = value;
            return value;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Logger.Error("Key could not be read", e);
            return null;
        }
    }

    /// <summary>Saves a key. Returns an error message, or null.</summary>
    public string? SaveKey(SecretKey key, string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "Paste a key first.";
        try
        {
            _secrets.Save(key, value);
            _cachedKeys[key] = value;
            KeyChecks.Remove(key);
            RefreshStoredKeys();
            return null;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Logger.Error("Key could not be saved", e);
            return e.Message;
        }
    }

    public void DeleteKey(SecretKey key)
    {
        try { _secrets.Delete(key); }
        catch (System.ComponentModel.Win32Exception e) { Logger.Error("Key could not be removed", e); }
        _cachedKeys.Remove(key);
        KeyChecks.Remove(key);
        RefreshStoredKeys();
    }

    public async Task<KeyCheckResult> CheckKeyAsync(SecretKey key)
    {
        var value = GetKey(key);
        var result = value == null ? new KeyCheckResult(KeyCheckStatus.Failed, "no key saved") : await new KeyTester().CheckAsync(key, value);
        KeyChecks[key] = result;
        KeysChanged?.Invoke();
        return result;
    }

    void RefreshStoredKeys()
    {
        _storedKeys.Clear();
        foreach (var key in Enum.GetValues<SecretKey>())
        {
            try { if (_secrets.Contains(key)) _storedKeys.Add(key); }
            catch (System.ComponentModel.Win32Exception e) { Logger.Error("Credential Manager could not be read", e); }
        }
        KeysChanged?.Invoke();
    }
}
