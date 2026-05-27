using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAutoAI.Agent;
using Serilog;

namespace DesktopAutoAI.Skills;

/// <summary>
/// On-disk skills cache. One JSON file per <c>sha256(app + ":" +
/// normalized_goal)</c>, stored under
/// <c>%LOCALAPPDATA%\DesktopAutoAI\skills\</c> by default. Snake_case JSON
/// to match the rest of the project's serialization style.
/// </summary>
public sealed class SkillsCache
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; }

    public SkillsCache(string? overrideDirectory = null)
    {
        Directory = overrideDirectory ?? DefaultDirectory();
        System.IO.Directory.CreateDirectory(Directory);
    }

    public static string DefaultDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopAutoAI", "skills");

    public static string ComputeKey(string appExecutable, string goal)
    {
        var key = $"{appExecutable.Trim().ToLowerInvariant()}:{NormalizeGoal(goal)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string NormalizeGoal(string goal)
    {
        if (string.IsNullOrWhiteSpace(goal)) return string.Empty;
        var lower = goal.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        bool inWs = false;
        foreach (var c in lower)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWs) sb.Append(' ');
                inWs = true;
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }
        return sb.ToString().Trim();
    }

    public Skill? TryLoad(string appExecutable, string goal)
    {
        var key = ComputeKey(appExecutable, goal);
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Skill>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read skill cache file {Path}; ignoring.", path);
            return null;
        }
    }

    public void Save(string appExecutable, string goal, IReadOnlyList<AgentAction> actions)
        => Save(appExecutable, goal, actions, steps: null);

    /// <summary>Save a batch-mode skill, preserving each step's verified
    /// postcondition so replay can self-check against app drift.</summary>
    public void SaveSteps(string appExecutable, string goal, IReadOnlyList<PlanStep> steps)
    {
        var actions = steps.Select(s => s.Action).ToList();
        Save(appExecutable, goal, actions, steps);
    }

    private void Save(string appExecutable, string goal, IReadOnlyList<AgentAction> actions, IReadOnlyList<PlanStep>? steps)
    {
        if (actions.Count == 0)
        {
            Log.Debug("Refusing to save empty skill for goal '{Goal}'.", goal);
            return;
        }

        var key = ComputeKey(appExecutable, goal);
        var path = PathFor(key);

        Skill? existing = null;
        if (File.Exists(path))
        {
            try { existing = JsonSerializer.Deserialize<Skill>(File.ReadAllText(path), JsonOpts); }
            catch { /* corrupt file - we overwrite below */ }
        }

        var now = DateTimeOffset.UtcNow;
        var record = new Skill(
            CacheKey: key,
            AppExecutable: appExecutable.ToLowerInvariant(),
            Goal: goal,
            NormalizedGoal: NormalizeGoal(goal),
            CreatedAt: existing?.CreatedAt ?? now,
            LastUsedAt: now,
            SuccessCount: (existing?.SuccessCount ?? 0) + 1,
            Actions: actions,
            Steps: steps);

        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOpts));
        Log.Information("Saved skill ({Count} action(s){Verified}, run #{N}) to {Path}",
            actions.Count, steps is not null ? ", verified" : "", record.SuccessCount, path);
    }

    public bool Delete(string appExecutable, string goal)
    {
        var path = PathFor(ComputeKey(appExecutable, goal));
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public string PathFor(string cacheKey) => Path.Combine(Directory, $"{cacheKey}.json");
}
