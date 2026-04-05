using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Loads .sln/.slnx/.csproj files by shelling out to <c>dotnet</c> CLI commands.
/// Returns structured <see cref="ProjectInfo"/> records in dependency order,
/// with fully resolved reference paths (NuGet + framework DLLs).
/// </summary>
internal static class SolutionLoader
{
    private static readonly string[] SolutionExtensions = { ".sln", ".slnx" };
    private const string CsprojExtension = ".csproj";
    private const int ProcessTimeoutMs = 60_000;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="path"/> looks like a solution or project file
    /// the loader can handle.
    /// </summary>
    public static bool CanLoad(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(CsprojExtension, StringComparison.OrdinalIgnoreCase)
            || SolutionExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads a solution or single project file and returns every project in
    /// dependency-first (topological) order with resolved references.
    /// </summary>
    public static List<ProjectInfo> Load(string path, Action<string>? log = null)
    {
        log ??= Console.Error.WriteLine;
        var ext = Path.GetExtension(path);

        List<string> csprojPaths;
        if (ext.Equals(CsprojExtension, StringComparison.OrdinalIgnoreCase))
        {
            csprojPaths = new List<string> { Path.GetFullPath(path) };
        }
        else
        {
            log($"Loading solution: {path}");
            csprojPaths = ListSolutionProjects(path, log);
            log($"  Found {csprojPaths.Count} project(s)");
        }

        // Load metadata + source files + project references for each project.
        var projects = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in csprojPaths)
        {
            var info = LoadProject(csproj, log);
            if (info != null)
                projects[info.ProjectPath] = info;
        }

        // Topological sort so dependencies come before dependents.
        var sorted = TopologicalSort(projects, log);

        // Resolve assembly references (NuGet + framework) for each project.
        foreach (var project in sorted)
        {
            ResolveReferences(project, log);
        }

        return sorted;
    }

    // ── Solution parsing ─────────────────────────────────────────────────────

    private static List<string> ListSolutionProjects(string solutionPath, Action<string> log)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        var solutionDir = Path.GetDirectoryName(fullPath)!;

        var (exitCode, stdout, stderr) = RunDotnet($"sln \"{fullPath}\" list", solutionDir);

        if (exitCode != 0)
        {
            log($"Warning: 'dotnet sln list' failed (exit {exitCode}): {stderr}");
            return new List<string>();
        }

        // Output format:
        //   Project(s)
        //   ----------
        //   Relative\Path\To\Project.csproj
        //   ...
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(CsprojExtension, StringComparison.OrdinalIgnoreCase))
            .Select(relative => Path.GetFullPath(Path.Combine(solutionDir, relative)))
            .Where(full =>
            {
                if (File.Exists(full)) return true;
                log($"Warning: project not found on disk — skipping: {full}");
                return false;
            })
            .ToList();

        return lines;
    }

    // ── Single project loading ───────────────────────────────────────────────

    private static ProjectInfo? LoadProject(string csprojPath, Action<string> log)
    {
        var projectDir = Path.GetDirectoryName(csprojPath)!;

        // Fetch properties.
        var props = GetMSBuildProperties(
            csprojPath, projectDir, log,
            "TargetFramework", "RootNamespace", "AssemblyName", "OutputType");

        if (props == null)
        {
            log($"Warning: could not read properties for {csprojPath} — skipping.");
            return null;
        }

        // Fetch items: source files + project references.
        var items = GetMSBuildItems(csprojPath, projectDir, log, "Compile", "ProjectReference");
        if (items == null)
        {
            log($"Warning: could not read items for {csprojPath} — skipping.");
            return null;
        }

        var sourceFiles = ExtractFullPaths(items, "Compile");
        var projectRefs = ExtractFullPaths(items, "ProjectReference");

        log($"  {Path.GetFileName(csprojPath)}: {sourceFiles.Count} source(s), " +
            $"{projectRefs.Count} project ref(s)");

        return new ProjectInfo(
            projectPath: Path.GetFullPath(csprojPath),
            assemblyName: props.GetValueOrDefault("AssemblyName") ?? Path.GetFileNameWithoutExtension(csprojPath),
            rootNamespace: props.GetValueOrDefault("RootNamespace") ?? "",
            targetFramework: props.GetValueOrDefault("TargetFramework") ?? "",
            outputType: props.GetValueOrDefault("OutputType") ?? "Library",
            sourceFiles: sourceFiles,
            projectReferences: projectRefs,
            referencePaths: new List<string>());
    }

    // ── Reference resolution ─────────────────────────────────────────────────

    private static void ResolveReferences(ProjectInfo project, Action<string> log)
    {
        var projectDir = Path.GetDirectoryName(project.ProjectPath)!;

        // -t:ResolveAssemblyReferences forces MSBuild to evaluate the full
        // reference closure, then --getItem:ReferencePath extracts the paths.
        var (exitCode, stdout, stderr) = RunDotnet(
            $"msbuild \"{project.ProjectPath}\" -t:ResolveAssemblyReferences --getItem:ReferencePath",
            projectDir);

        if (exitCode != 0)
        {
            log($"Warning: reference resolution failed for {Path.GetFileName(project.ProjectPath)}: {stderr}");
            return;
        }

        var refPaths = ParseReferencePaths(stdout, log);
        project.ReferencePaths.AddRange(refPaths);

        int nugetCount = refPaths.Count(p => p.Contains(".nuget", StringComparison.OrdinalIgnoreCase));
        int fwCount = refPaths.Count - nugetCount;
        log($"  {Path.GetFileName(project.ProjectPath)}: {nugetCount} NuGet ref(s), {fwCount} framework ref(s)");
    }

    private static List<string> ParseReferencePaths(string json, Action<string> log)
    {
        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Items", out var items) &&
                items.TryGetProperty("ReferencePath", out var refs))
            {
                foreach (var refEl in refs.EnumerateArray())
                {
                    var identity = refEl.GetProperty("Identity").GetString();
                    if (!string.IsNullOrEmpty(identity) && File.Exists(identity))
                        paths.Add(identity);
                }
            }
        }
        catch (JsonException ex)
        {
            log($"Warning: could not parse ReferencePath JSON: {ex.Message}");
        }
        return paths;
    }

    // ── MSBuild JSON helpers ─────────────────────────────────────────────────

    private static Dictionary<string, string>? GetMSBuildProperties(
        string csprojPath, string workingDir, Action<string> log,
        params string[] propertyNames)
    {
        var propArgs = string.Join(" ", propertyNames.Select(p => $"--getProperty:{p}"));
        var (exitCode, stdout, stderr) = RunDotnet(
            $"msbuild \"{csprojPath}\" {propArgs}", workingDir);

        if (exitCode != 0)
        {
            log($"Warning: getProperty failed: {stderr}");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("Properties", out var props))
                return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in propertyNames)
            {
                if (props.TryGetProperty(name, out var val))
                    result[name] = val.GetString() ?? "";
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? GetMSBuildItems(
        string csprojPath, string workingDir, Action<string> log,
        params string[] itemNames)
    {
        var itemArgs = string.Join(" ", itemNames.Select(i => $"--getItem:{i}"));
        var (exitCode, stdout, stderr) = RunDotnet(
            $"msbuild \"{csprojPath}\" {itemArgs}", workingDir);

        if (exitCode != 0)
        {
            log($"Warning: getItem failed: {stderr}");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                // Deserialize into a standalone JsonElement that owns its own memory
                // (avoids leaking a JsonDocument).
                return JsonSerializer.Deserialize<JsonElement>(items.GetRawText());
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ExtractFullPaths(JsonElement? items, string itemName)
    {
        var paths = new List<string>();
        if (items == null) return paths;

        if (items.Value.TryGetProperty(itemName, out var array))
        {
            foreach (var el in array.EnumerateArray())
            {
                var fullPath = el.TryGetProperty("FullPath", out var fp) ? fp.GetString() : null;
                if (!string.IsNullOrEmpty(fullPath))
                    paths.Add(fullPath);
            }
        }
        return paths;
    }

    // ── Topological sort ─────────────────────────────────────────────────────

    private static List<ProjectInfo> TopologicalSort(
        Dictionary<string, ProjectInfo> projects, Action<string> log)
    {
        var sorted  = new List<ProjectInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects.Values)
            Visit(project, projects, sorted, visited, inStack, log);

        return sorted;
    }

    private static void Visit(
        ProjectInfo project,
        Dictionary<string, ProjectInfo> projects,
        List<ProjectInfo> sorted,
        HashSet<string> visited,
        HashSet<string> inStack,
        Action<string> log)
    {
        if (visited.Contains(project.ProjectPath))
            return;

        if (!inStack.Add(project.ProjectPath))
        {
            log($"Warning: circular project reference detected at {Path.GetFileName(project.ProjectPath)}");
            return;
        }

        // Visit dependencies first.
        foreach (var depPath in project.ProjectReferences)
        {
            if (projects.TryGetValue(depPath, out var dep))
                Visit(dep, projects, sorted, visited, inStack, log);
        }

        inStack.Remove(project.ProjectPath);
        visited.Add(project.ProjectPath);
        sorted.Add(project);
    }

    // ── Process execution ────────────────────────────────────────────────────

    private static (int ExitCode, string Stdout, string Stderr) RunDotnet(
        string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;

        // Read stderr asynchronously to avoid deadlock when the child process
        // fills one pipe buffer before the other is consumed.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.Result;

        if (!process.WaitForExit(ProcessTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, stdout, "Process timed out and was killed.");
        }

        return (process.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// Metadata for a single .csproj project, resolved from MSBuild evaluation.
/// </summary>
internal sealed class ProjectInfo
{
    public ProjectInfo(
        string projectPath,
        string assemblyName,
        string rootNamespace,
        string targetFramework,
        string outputType,
        List<string> sourceFiles,
        List<string> projectReferences,
        List<string> referencePaths)
    {
        ProjectPath = projectPath;
        AssemblyName = assemblyName;
        RootNamespace = rootNamespace;
        TargetFramework = targetFramework;
        OutputType = outputType;
        SourceFiles = sourceFiles;
        ProjectReferences = projectReferences;
        ReferencePaths = referencePaths;
    }

    public string ProjectPath { get; }
    public string AssemblyName { get; }
    public string RootNamespace { get; }
    public string TargetFramework { get; }
    public string OutputType { get; }
    public List<string> SourceFiles { get; }
    public List<string> ProjectReferences { get; }
    public List<string> ReferencePaths { get; }
}
