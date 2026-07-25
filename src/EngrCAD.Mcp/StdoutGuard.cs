namespace EngrCAD.Mcp;

/// <summary>
/// Makes standard output safe to speak JSON-RPC on.
/// <para><b>The footgun this exists for:</b> the MCP stdio transport <i>is</i> stdout.
/// A single stray <c>Console.WriteLine</c> — from the design program, from a library,
/// from <c>EngrCad.Run</c>'s own "wrote part.step" reporting — lands in the middle of
/// the frame stream and every connected client breaks, usually with an unhelpful parse
/// error. Routing the <see cref="Viewer.IEngrCadLog"/> seam to stderr is not enough,
/// because model code is arbitrary user code.</para>
/// <para><b>What it does:</b> takes the real stdout <i>handle</i> for the protocol and
/// then points <see cref="Console.Out"/> at stderr. Anything written through
/// <c>Console.Write*</c> afterwards — by any code, anywhere in the process — goes to
/// stderr, where MCP clients conventionally surface it as server logging. The protocol
/// stream is a separate <see cref="Stream"/> over the same handle, so it is untouched
/// by the redirection.</para>
/// <para>The one thing it cannot defend against is code that opens the standard output
/// handle itself (<c>Console.OpenStandardOutput</c>) or writes to file descriptor 1
/// natively. Nothing in EngrCAD does.</para>
/// </summary>
public sealed class StdoutGuard : IDisposable
{
    private readonly TextWriter _previousOut;
    private bool _released;

    private StdoutGuard(Stream protocolOutput, TextWriter previousOut)
    {
        ProtocolOutput = protocolOutput;
        _previousOut = previousOut;
    }

    /// <summary>The raw standard-output stream reserved for protocol frames. Write
    /// nothing else to it.</summary>
    public Stream ProtocolOutput { get; }

    /// <summary>Claims stdout for the protocol and redirects <see cref="Console.Out"/>
    /// to stderr. Dispose to restore the previous <see cref="Console.Out"/> (tests do;
    /// a server process simply exits).</summary>
    public static StdoutGuard Claim()
    {
        // Order does not actually matter — OpenStandardOutput goes to the handle, not
        // to Console.Out — but taking the protocol stream first states the intent.
        var protocolOutput = Console.OpenStandardOutput();
        var previousOut = Console.Out;
        Console.SetOut(Console.Error);
        return new StdoutGuard(protocolOutput, previousOut);
    }

    /// <summary>Restores the previous <see cref="Console.Out"/>. The protocol stream is
    /// deliberately NOT disposed — it belongs to the process's standard output.</summary>
    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        Console.SetOut(_previousOut);
    }
}
