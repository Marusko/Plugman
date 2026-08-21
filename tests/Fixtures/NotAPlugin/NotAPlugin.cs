namespace NotAPlugin;

/// <summary>
/// A perfectly valid assembly that implements nothing. Scanning it must produce a recorded
/// load error, not an exception.
/// </summary>
public sealed class JustAClass
{
    public string Greet() => "I am not a plugin.";
}
