using Xunit;

namespace LiveStream.DatabaseTests;

/// <summary>
/// A fact that needs a real PostgreSQL container.
///
/// When Docker is unavailable the test is reported as <em>skipped</em>, never as passed: an
/// environment that cannot run these checks must say so rather than imply the schema was verified.
/// </summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker is not available on this machine, so the PostgreSQL migration tests cannot run.";
        }
    }
}

internal static class DockerAvailability
{
    private static readonly Lazy<bool> Available = new(Detect);

    public static bool IsAvailable => Available.Value;

    private static bool Detect()
    {
        // An explicit endpoint means the caller has told Testcontainers where the daemon is.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var executables = OperatingSystem.IsWindows() ? new[] { "docker.exe" } : ["docker"];

        return pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => executables.Any(executable => SafeFileExists(directory, executable)));
    }

    private static bool SafeFileExists(string directory, string fileName)
    {
        try
        {
            return File.Exists(Path.Combine(directory, fileName));
        }
        catch (ArgumentException)
        {
            return false; // Malformed PATH entry.
        }
    }
}
