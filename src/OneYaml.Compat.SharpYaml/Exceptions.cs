using System;

namespace OneYaml.Compat.SharpYaml;

/// <summary>The error raised for YAML that cannot be read, or a value that cannot be bound or written.</summary>
public class YamlException : Exception
{
    /// <summary>Creates an exception.</summary>
    public YamlException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public YamlException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public YamlException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public YamlException(Mark start, Mark end, string message)
        : this(start, end, message, null)
    {
    }

    /// <summary>Creates an exception for a span of the source, with a cause.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public YamlException(Mark start, Mark end, string message, Exception? innerException)
        : base(Format(null, start, end, message), innerException)
    {
        Start = start;
        End = end;
    }

    /// <summary>Creates an exception for a span of a named source.</summary>
    /// <param name="sourceName">The name of the source, such as a file name.</param>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public YamlException(string? sourceName, Mark start, Mark end, string message)
        : this(sourceName, start, end, message, null)
    {
    }

    /// <summary>Creates an exception for a span of a named source, with a cause.</summary>
    /// <param name="sourceName">The name of the source, such as a file name.</param>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public YamlException(string? sourceName, Mark start, Mark end, string message, Exception? innerException)
        : base(Format(sourceName, start, end, message), innerException)
    {
        SourceName = sourceName;
        Start = start;
        End = end;
    }

    /// <summary>The name of the source, when one was given.</summary>
    public string? SourceName { get; }

    /// <summary>Where the problem starts.</summary>
    public Mark Start { get; }

    /// <summary>Where the problem ends.</summary>
    public Mark End { get; }

    private static string Format(string? sourceName, Mark start, Mark end, string message) =>
        string.IsNullOrEmpty(sourceName) ? $"({start}) - ({end}): {message}" : $"{sourceName}: ({start}) - ({end}): {message}";
}

/// <summary>The text is not well-formed YAML.</summary>
public class SyntaxErrorException : YamlException
{
    /// <summary>Creates an exception.</summary>
    public SyntaxErrorException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SyntaxErrorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public SyntaxErrorException(Mark start, Mark end, string message)
        : base(start, end, message)
    {
    }

    internal SyntaxErrorException(string? sourceName, Mark start, Mark end, string message)
        : base(sourceName, start, end, message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SyntaxErrorException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The text is valid characters but not a valid YAML structure.</summary>
public class SemanticErrorException : YamlException
{
    /// <summary>Creates an exception.</summary>
    public SemanticErrorException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SemanticErrorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public SemanticErrorException(Mark start, Mark end, string message)
        : base(start, end, message)
    {
    }

    internal SemanticErrorException(string? sourceName, Mark start, Mark end, string message)
        : base(sourceName, start, end, message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SemanticErrorException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
