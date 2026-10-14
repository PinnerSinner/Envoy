namespace Envoy.Core.Capture;

/// <summary>Ids for <c>Capture.Backend</c> (REQ-CAP-35, 36).</summary>
public static class CaptureBackendIds
{
    /// <summary>The GDI screen copy: works for windowed and borderless games; cannot see covered windows.</summary>
    public const string Gdi = "gdi";
}

/// <summary>
/// A capture backend could not read the screen (for example on the secure desktop while Windows is locked). The message
/// is for the log only; the user sees <see cref="CaptureText.ScreenUnreadable"/> (see <see cref="CaptureErrors"/>).
/// </summary>
public sealed class CaptureException : Exception
{
    public CaptureException()
    {
    }

    public CaptureException(string message) : base(message)
    {
    }

    public CaptureException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
