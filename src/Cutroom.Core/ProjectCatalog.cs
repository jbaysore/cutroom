namespace Cutroom.Core;

public sealed record ProjectEntry(string Path, string Name, DateTime SavedAt, string? Error);

public sealed class ProjectCatalog(AppSettings settings)
{
    public const string Extension = ".cutroom.json";
    public string Root => settings.ProjectsFolder ?? AppSettings.DefaultProjectsFolder;

    public IReadOnlyList<ProjectEntry> Read(out int unavailableLocations)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int errors = 0;

        void AddFiles(string folder)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*" + Extension))
                    paths.Add(Path.GetFullPath(file));
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (IsProjectError(ex)) { errors++; }
        }

        foreach (var root in new[] { Root, AppSettings.DefaultProjectsFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddFiles(root);
            try
            {
                foreach (var folder in Directory.EnumerateDirectories(root)) AddFiles(folder);
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (IsProjectError(ex)) { errors++; }
        }

        foreach (var path in settings.RecentProjects)
        {
            try
            {
                if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    paths.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (IsProjectError(ex)) { errors++; }
        }
        unavailableLocations = errors;

        return paths.Select(ReadEntry).OrderByDescending(project => project.SavedAt)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static ProjectEntry ReadEntry(string path)
    {
        var fallback = Path.GetFileName(path)[..^Extension.Length];
        var savedAt = DateTime.MinValue;
        try
        {
            savedAt = File.GetLastWriteTime(path);
            // Listing only needs metadata. Opening validates and migrates the complete document.
            using var stream = File.OpenRead(path);
            using var json = System.Text.Json.JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new InvalidDataException("This file is not a Cutroom project.");
            var name = root.TryGetProperty("Name", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString() : null;
            return new(path, string.IsNullOrWhiteSpace(name) ? fallback : name, savedAt, null);
        }
        catch (Exception ex) when (IsProjectError(ex)) { return new(path, fallback, savedAt, ex.Message); }
    }

    public Document Create(string name)
    {
        name = ValidName(name);
        var stem = Derive.SafeFileName(name).TrimEnd('.', ' ');
        if (stem.Length > 80)
        {
            int length = char.IsHighSurrogate(stem[79]) ? 79 : 80;
            stem = stem[..length].TrimEnd('.', ' ');
        }
        if (stem.Length == 0) stem = "Project";
        var device = stem.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && "0123456789¹²³".Contains(device[3])))
            stem = "_" + stem;

        for (int index = 1; ; index++)
        {
            var leaf = index == 1 ? stem : $"{stem} ({index})";
            var folder = Path.Combine(Root, leaf);
            if (Directory.Exists(folder) || File.Exists(folder)) continue;
            var path = Path.GetFullPath(Path.Combine(folder, leaf + Extension));
            var document = new Document(Project.CreateDefault(name), path);
            try
            {
                document.SaveNew();
                return document;
            }
            catch (IOException) when (File.Exists(path)) { }
        }
    }

    public void Remember(string path)
    {
        var fullPath = Path.GetFullPath(path);
        settings.RecentProjects.RemoveAll(project => string.Equals(project, fullPath, StringComparison.OrdinalIgnoreCase));
        settings.RecentProjects.Insert(0, fullPath);
        settings.Save();
    }

    internal static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a project name on one line.");
        return name;
    }

    public static bool IsProjectError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException;
}
