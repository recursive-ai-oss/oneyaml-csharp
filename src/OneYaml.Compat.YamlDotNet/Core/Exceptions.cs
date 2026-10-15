using System;

namespace OneYaml.Compat.YamlDotNet.Core;

/// <summary>The base class of the errors raised while reading or writing YAML.</summary>
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
    /// <param name="inner">The cause.</param>
    public YamlException(string message, Exception? inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public YamlException(in Mark start, in Mark end, string message)
        : this(start, end, message, null)
    {
    }

    /// <summary>Creates an exception for a span of the source, with a cause.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public YamlException(in Mark start, in Mark end, string message, Exception? inner)
        : base(message, inner)
    {
        Start = start;
        End = end;
    }

    /// <summary>Where the problem starts.</summary>
    public Mark Start { get; }

    /// <summary>Where the problem ends.</summary>
    public Mark End { get; }

    /// <inheritdoc/>
    public override string ToString() => Start == Mark.Empty && End == Mark.Empty ? base.ToString() : $"({Start}) - ({End}): {Message}" + (InnerException is null ? string.Empty : Environment.NewLine + InnerException);
}

/// <summary>The text is valid characters but not valid YAML structure.</summary>
public class SemanticErrorException : YamlException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">The message.</param>
    public SemanticErrorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public SemanticErrorException(in Mark start, in Mark end, string message)
        : base(start, end, message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public SemanticErrorException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>The text breaks the YAML syntax.</summary>
public sealed class SyntaxErrorException : YamlException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">The message.</param>
    public SyntaxErrorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public SyntaxErrorException(in Mark start, in Mark end, string message)
        : base(start, end, message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public SyntaxErrorException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>An alias names an anchor that is not defined.</summary>
public class AnchorNotFoundException : YamlException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">The message.</param>
    public AnchorNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public AnchorNotFoundException(in Mark start, in Mark end, string message)
        : base(start, end, message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public AnchorNotFoundException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>The input or the object graph nests deeper than the configured recursion limit, or an expansion limit was exceeded.</summary>
public sealed class MaximumRecursionLevelReachedException : YamlException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">The message.</param>
    public MaximumRecursionLevelReachedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception for a span of the source.</summary>
    /// <param name="start">Where the problem starts.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="message">The message.</param>
    public MaximumRecursionLevelReachedException(in Mark start, in Mark end, string message)
        : base(start, end, message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public MaximumRecursionLevelReachedException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
