using System.Text;
using System.Text.Json;

namespace AAEmu.Game.Bots.Population.Identity;

/// <summary>
/// Versioned file persistence boundary for a bot roster. The supplied lookup is the
/// seam to AAEmu's authoritative character store; every read and write fails closed
/// when an identity is no longer present there.
/// </summary>
public sealed class JsonBotRosterStore : IBotRosterStore
{
    private sealed class RosterDocument
    {
        public string SchemaVersion { get; set; }
        public List<RosterEntryDocument> Entries { get; set; }
    }

    private sealed class RosterEntryDocument
    {
        public uint CharacterId { get; set; }
        public bool Enabled { get; set; }
        public string Profile { get; set; }
        public uint HomeZoneId { get; set; }
        public string DesiredLifeState { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<uint, bool> _authoritativeCharacterExists;

    public JsonBotRosterStore(string path, Func<uint, bool> authoritativeCharacterExists)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A roster path is required.", nameof(path));

        _path = Path.GetFullPath(path);
        _authoritativeCharacterExists = authoritativeCharacterExists
            ?? throw new ArgumentNullException(nameof(authoritativeCharacterExists));
    }

    public BotRosterSnapshot Read()
    {
        lock (_gate)
            return ReadCore();
    }

    /// <summary>Remove only identities positively confirmed archived by the host.
    /// Preserve the original roster before repairing historical deletion entries.</summary>
    public IReadOnlyList<uint> RemoveArchivedCharacters(Func<uint, bool> isArchived)
    {
        ArgumentNullException.ThrowIfNull(isArchived);
        lock (_gate)
        {
            var snapshot = ReadCore(validateAuthority: false);
            var removed = snapshot.Entries.Where(entry => isArchived(entry.Identity.CharacterId))
                .Select(entry => entry.Identity.CharacterId).ToHashSet();
            if (removed.Count == 0) return Array.Empty<uint>();
            var remaining = snapshot.Entries.Where(entry => !removed.Contains(entry.Identity.CharacterId)).ToArray();
            foreach (var entry in remaining) EnsureAuthoritative(entry.Identity.CharacterId);
            File.Copy(_path, _path + ".before-archive-cleanup-" + Guid.NewGuid().ToString("N") + ".bak");
            WriteCore(remaining);
            return removed.Order().ToArray();
        }
    }

    public BotRosterEntry Create(BotRosterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            var snapshot = ReadCore();
            EnsureAuthoritative(entry.Identity.CharacterId);
            if (snapshot.Entries.Any(existing => existing.Identity == entry.Identity))
                throw new InvalidOperationException($"Bot character id {entry.Identity.CharacterId} already exists in the roster.");

            WriteCore(snapshot.Entries.Append(entry));
            return entry;
        }
    }

    public BotRosterEntry Update(BotRosterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            var snapshot = ReadCore();
            EnsureAuthoritative(entry.Identity.CharacterId);
            if (!snapshot.Entries.Any(existing => existing.Identity == entry.Identity))
                throw new KeyNotFoundException($"Bot character id {entry.Identity.CharacterId} is not in the roster.");

            WriteCore(snapshot.Entries.Select(existing => existing.Identity == entry.Identity ? entry : existing));
            return entry;
        }
    }

    /// <summary>
    /// Compensates only an identity creation that has not been returned as
    /// successful. This is deliberately not exposed as a retirement command.
    /// </summary>
    public bool RemoveForCreationRollback(BotIdentity identity)
    {
        lock (_gate)
        {
            var snapshot = ReadCore();
            if (!snapshot.Entries.Any(existing => existing.Identity == identity))
                return true;

            WriteCore(snapshot.Entries.Where(existing => existing.Identity != identity));
            return true;
        }
    }

    private BotRosterSnapshot ReadCore(bool validateAuthority = true)
    {
        if (!File.Exists(_path))
            return new BotRosterSnapshot([]);

        RosterDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RosterDocument>(File.ReadAllText(_path), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The bot roster is not valid JSON.", exception);
        }

        if (document == null)
            throw new InvalidDataException("The bot roster document is empty.");
        if (!string.Equals(document.SchemaVersion, BotRosterSnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported bot roster schema version '{document.SchemaVersion ?? "<missing>"}'.");
        if (document.Entries == null)
            throw new InvalidDataException("The bot roster entries collection is missing.");

        var identities = new HashSet<uint>();
        var entries = new List<BotRosterEntry>(document.Entries.Count);
        foreach (var persisted in document.Entries)
        {
            if (!identities.Add(persisted.CharacterId))
                throw new InvalidDataException($"Duplicate bot character id {persisted.CharacterId} in the roster.");

            if (validateAuthority) EnsureAuthoritative(persisted.CharacterId);
            try
            {
                entries.Add(new BotRosterEntry(
                    new BotIdentity(persisted.CharacterId),
                    persisted.Enabled,
                    persisted.Profile,
                    persisted.HomeZoneId,
                    persisted.DesiredLifeState));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Bot roster entry {persisted.CharacterId} is invalid.",
                    exception);
            }
        }

        return new BotRosterSnapshot(entries);
    }

    private void WriteCore(IEnumerable<BotRosterEntry> entries)
    {
        var ordered = entries.OrderBy(entry => entry.Identity.CharacterId).ToArray();
        foreach (var entry in ordered)
            EnsureAuthoritative(entry.Identity.CharacterId);

        var document = new RosterDocument
        {
            SchemaVersion = BotRosterSnapshot.CurrentSchemaVersion,
            Entries = ordered.Select(entry => new RosterEntryDocument
            {
                CharacterId = entry.Identity.CharacterId,
                Enabled = entry.Enabled,
                Profile = entry.Profile,
                HomeZoneId = entry.HomeZoneId,
                DesiredLifeState = entry.DesiredLifeState
            }).ToList()
        };

        var parent = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var json = JsonSerializer.Serialize(document, JsonOptions).ReplaceLineEndings("\n") + "\n";
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureAuthoritative(uint characterId)
    {
        if (characterId == 0 || !_authoritativeCharacterExists(characterId))
            throw new InvalidDataException($"Bot character id {characterId} is not an authoritative AAEmu character identity.");
    }
}
