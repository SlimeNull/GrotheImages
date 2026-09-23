using System;

namespace GrotheImages;

public sealed class GrotheImageException : Exception
{
    public GrotheImageException(string message) : base(message)
    {
    }

    public GrotheImageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
