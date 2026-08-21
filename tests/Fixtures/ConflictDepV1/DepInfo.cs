namespace ConflictDep;

/// <summary>Reports which version of this dependency actually got loaded.</summary>
public static class DepInfo
{
    public const string Version = "1.0.0";

    public static string Describe() =>
        $"ConflictDep {Version} from {typeof(DepInfo).Assembly.GetName().Version}";
}
