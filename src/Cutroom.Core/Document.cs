using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cutroom.Core;

public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };

    public static byte[] Serialize(Project p) => JsonSerializer.SerializeToUtf8Bytes(p, Options);
    public static string SerializePretty(Project p) => JsonSerializer.Serialize(p, Pretty);

    public static Project Deserialize(ReadOnlySpan<byte> utf8)
    {
        var node = JsonNode.Parse(utf8) as JsonObject ?? throw new InvalidDataException("Not a Cutroom project.");
        Migrate(node);
        return node.Deserialize<Project>(Options) ?? throw new InvalidDataException("Not a Cutroom project.");
    }

    /// <summary>Upgrades older project documents in place. Version 0 is any file written before versioning.</summary>
    public static void Migrate(JsonObject node)
    {
        int version = node["Version"]?.GetValue<int>() ?? 0;
        if (version > Project.CurrentVersion)
            throw new InvalidDataException("This project was saved by a newer version of Cutroom.");
        if (version < 1)
        {
            node["Tracks"] ??= new JsonArray();
            node["Version"] = 1;
        }
    }
}

/// <summary>
/// The open project plus its history. Every change goes through <see cref="Edit"/>, which snapshots the
/// project first, so undo always restores exactly the previous state.
/// </summary>
public sealed class Document
{
    readonly List<(string Label, byte[] State)> undo = [];
    readonly List<(string Label, byte[] State)> redo = [];
    DateTime lastSave = DateTime.MinValue;

    public Project Project { get; private set; }
    public string? Path { get; set; }
    public bool Dirty { get; private set; }
    public int Revision { get; private set; }
    public event Action? Changed;

    public Document(Project project, string? path = null) { Project = project; Path = path; }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string? RedoLabel => redo.Count > 0 ? redo[^1].Label : null;

    public void Edit(string label, Action<Project> change)
    {
        var before = ProjectJson.Serialize(Project);
        try { change(Project); }
        catch
        {
            Project = ProjectJson.Deserialize(before);
            throw;
        }
        undo.Add((label, before));
        redo.Clear();
        Touch();
    }

    public T Edit<T>(string label, Func<Project, T> change)
    {
        T result = default!;
        Edit(label, p => { result = change(p); });
        return result;
    }

    public bool Undo()
    {
        if (undo.Count == 0) return false;
        var (label, state) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add((label, ProjectJson.Serialize(Project)));
        Project = ProjectJson.Deserialize(state);
        Touch();
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        var (label, state) = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add((label, ProjectJson.Serialize(Project)));
        Project = ProjectJson.Deserialize(state);
        Touch();
        return true;
    }

    void Touch()
    {
        Dirty = true;
        Revision++;
        Changed?.Invoke();
    }

    public static Document Open(string path) => new(ProjectJson.Deserialize(File.ReadAllBytes(path)), path);

    /// <summary>Writes atomically and rotates name.cutroom.json.bak1..5.</summary>
    public void Save(string? path = null)
    {
        path ??= Path ?? throw new InvalidOperationException("Choose where to save the project first.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temp = path + ".partial";
        File.WriteAllText(temp, ProjectJson.SerializePretty(Project));
        if (File.Exists(path))
        {
            for (int i = 4; i >= 1; i--)
            {
                var from = $"{path}.bak{i}";
                if (File.Exists(from)) File.Move(from, $"{path}.bak{i + 1}", true);
            }
            File.Copy(path, path + ".bak1", true);
        }
        File.Move(temp, path, true);
        Path = path;
        Dirty = false;
        lastSave = DateTime.UtcNow;
    }

    /// <summary>Called on a timer; saves when there are changes and 30 s have passed (or immediately when forced).</summary>
    public bool AutoSave(bool force = false)
    {
        if (!Dirty || Path is null) return false;
        if (!force && DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(30)) return false;
        Save();
        return true;
    }
}
