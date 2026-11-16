using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PriorAuthBot.Core.LocalModels;

/// <summary>
/// models.lock.json (chapter 15): what Directory.Packages.props is for packages, this file is for
/// model weights. Every local model PriorAuthBot may load is listed with where it came from, the exact
/// revision, its license and whether that license allows Deductible Dynamics' use, and the SHA-256 of
/// every file. A model that is not in the file, or whose files do not match it, is not loaded.
/// </summary>
public sealed record ModelLock(string? Description, IReadOnlyList<LockedModel> Models)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ModelLock Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return JsonSerializer.Deserialize<ModelLock>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"{path} is empty.");
    }

    /// <summary>
    /// Finds <paramref name="path"/> as given, or, when it is relative and not there, in each parent of
    /// the working directory: the API runs from src/PriorAuthBot.Api and the tests from bin folders.
    /// </summary>
    public static string? Find(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || File.Exists(path))
        {
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, path);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public LockedModel? Get(string id) => Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
}

/// <summary>One model, pinned.</summary>
/// <param name="Id">The name PriorAuthBot uses for it: settings, reports, prices, and the audit log.</param>
/// <param name="Kind"><c>OnnxGenAI</c>, <c>CrossEncoder</c>, or <c>Ollama</c>.</param>
/// <param name="Source">Where it came from, such as <c>huggingface:microsoft/Phi-4-mini-instruct-onnx</c> or <c>ollama:llama3.2:3b</c>.</param>
/// <param name="Revision">The exact revision: a Hugging Face commit, or the Ollama manifest digest.</param>
/// <param name="Path">The folder inside the source repository, for a model that is one folder of several.</param>
/// <param name="License">The license's SPDX identifier or name.</param>
/// <param name="LicenseUrl">Where the license text is.</param>
/// <param name="CommercialUse">Whether the license allows Deductible Dynamics' use. Someone who read it set this, not a script.</param>
/// <param name="ReviewedBy">Who approved the model, and when: the evidence a reviewer asks for.</param>
/// <param name="Files">Every file of the model, with its size and SHA-256. Empty for an Ollama model, which Ollama checks by digest.</param>
public sealed record LockedModel(
    string Id,
    string Kind,
    string Source,
    string? Revision,
    string? Path,
    string License,
    string? LicenseUrl,
    bool CommercialUse,
    string? ReviewedBy,
    IReadOnlyList<LockedFile>? Files)
{
    /// <summary>True when every file has a recorded hash. A model that is not pinned yet cannot be loaded.</summary>
    [JsonIgnore]
    public bool IsPinned => Kind == "Ollama"
        ? Revision is { Length: > 0 }
        : Files is { Count: > 0 } files && files.All(f => f.Sha256 is { Length: 64 });
}

public sealed record LockedFile(string Name, long? Bytes, string? Sha256);

/// <summary>What checking one model folder found. <see cref="Problems"/> is empty when it matched.</summary>
public sealed record ModelVerification(string ModelId, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>The model's files did not match models.lock.json, so it was not loaded.</summary>
public sealed class ModelIntegrityException : Exception
{
    public ModelIntegrityException()
    {
    }

    public ModelIntegrityException(string message)
        : base(message)
    {
    }

    public ModelIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Checks a model folder against its lock entry (chapter 15). Every listed file must be there, with
/// the recorded size and hash, and no other file that ONNX Runtime GenAI would read may be: a stray
/// <c>genai_config.json</c> overlay or an extra <c>.onnx</c> file changes what runs as surely as a
/// changed weight does.
/// </summary>
public static class ModelIntegrity
{
    /// <summary>Files a runtime reads from a model folder. Anything else there (a README, a license) is ignored.</summary>
    private static readonly string[] LoadedExtensions = [".onnx", ".data", ".json", ".txt", ".model", ".bin", ".safetensors"];

    /// <param name="model">The lock entry.</param>
    /// <param name="directory">The model folder.</param>
    /// <param name="requireCommercialUse">Treat a license that does not allow commercial use as a problem. True when loading a model to serve.</param>
    public static ModelVerification Verify(LockedModel model, string directory, bool requireCommercialUse = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var problems = new List<string>();
        if (!model.IsPinned)
        {
            problems.Add($"{model.Id} has no recorded hashes in models.lock.json. Run scripts/get-model.py {model.Id} --record, review the result, and commit it.");
            return new ModelVerification(model.Id, problems);
        }

        if (requireCommercialUse && !model.CommercialUse)
        {
            problems.Add($"{model.Id} is licensed {model.License}, which models.lock.json records as not allowing commercial use.");
        }

        if (!Directory.Exists(directory))
        {
            problems.Add($"The model folder {directory} does not exist.");
            return new ModelVerification(model.Id, problems);
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in model.Files ?? [])
        {
            listed.Add(file.Name);
            var full = System.IO.Path.Combine(directory, file.Name);
            if (!File.Exists(full))
            {
                problems.Add($"{file.Name} is missing.");
                continue;
            }

            var length = new FileInfo(full).Length;
            if (file.Bytes is { } bytes && bytes != length)
            {
                problems.Add($"{file.Name} is {length:N0} bytes; the lock says {bytes:N0}.");
                continue;
            }

            var hash = Sha256(full);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file.Name} has SHA-256 {hash}; the lock says {file.Sha256}.");
            }
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = System.IO.Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (!listed.Contains(name) && LoadedExtensions.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"{name} is in the model folder but not in the lock.");
            }
        }

        return new ModelVerification(model.Id, problems);
    }

    /// <summary>Verifies, and throws with every problem listed when the folder does not match.</summary>
    public static void EnsureVerified(LockedModel model, string directory)
    {
        var result = Verify(model, directory);
        if (!result.Ok)
        {
            throw new ModelIntegrityException($"Refusing to load {model.Id} from {directory}: " + string.Join(" ", result.Problems));
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
